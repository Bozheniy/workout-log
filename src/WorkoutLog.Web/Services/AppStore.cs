using System.Text.Json;
using Microsoft.JSInterop;
using WorkoutLog.Core;

namespace WorkoutLog.Web.Services;

public sealed class AppStore(
    BrowserStorage storage,
    GoogleSheetsGateway google,
    IJSRuntime javascript,
    IConfiguration configuration) : IAsyncDisposable
{
    private readonly SemaphoreSlim mutationGate = new(1, 1);
    private readonly SemaphoreSlim syncGate = new(1, 1);
    private DotNetObjectReference<AppStore>? reference;
    private Task? initialization;
    private LocalState? unsavedDraft;
    private bool reloadingForUpdate;

    public LocalState State { get; private set; } = new();
    public bool Ready { get; private set; }
    public bool Online { get; private set; } = true;
    public bool OfflineReady { get; private set; }
    public bool PersistentStorage { get; private set; }
    public bool Busy { get; private set; }
    public bool Syncing { get; private set; }
    public bool StorageFailed { get; private set; }
    public bool SignedIn { get; private set; }
    public bool LoginReady { get; private set; }
    public bool UpdateAvailable { get; private set; }
    public string? Error { get; private set; }
    public string? Notice { get; private set; }
    public DateTimeOffset? LastSavedAt { get; private set; }
    public event Action? Changed;
    public bool HasActiveWorkout => State.Sessions.Any(session => session.FinishedAt is null && !session.IsPaused);

    public Task InitializeAsync()
    {
        return initialization ??= InitializeCoreAsync();
    }

    private async Task InitializeCoreAsync()
    {
        try
        {
            State = await storage.LoadAsync() ?? new LocalState();
            if (State.SchemaVersion != 1)
            {
                throw new InvalidOperationException("Эта версия данных новее приложения. Обновите приложение.");
            }
            // Старые резервные копии могли не содержать настроек прогрессии.
            State.ProgressionSettings ??= [];
            if (string.IsNullOrEmpty(State.Settings.SpreadsheetUrl))
            {
                State.Settings.SpreadsheetUrl = configuration["DefaultSpreadsheetUrl"] ?? "";
            }
            if (string.IsNullOrWhiteSpace(State.Settings.GoogleClientId))
            {
                // Публичный ID уже настроен для сайта. Свою настройку пользователя сохраняем.
                State.Settings.GoogleClientId = configuration["DefaultGoogleClientId"] ?? "";
            }
            reference = DotNetObjectReference.Create(this);
            Online = await javascript.InvokeAsync<bool>("workout.initialize", reference);
            if (!State.Settings.TimerEnabled || !HasActiveWorkout)
            {
                await javascript.InvokeVoidAsync("workout.timer.stop");
            }
            OfflineReady = await javascript.InvokeAsync<bool>("workout.pwa.ready");
            PersistentStorage = await javascript.InvokeAsync<bool>("workout.storage.isPersistent");
            Ready = true;
            Notify();

            if (State.Plan is null && Online && State.Settings.SpreadsheetUrl.Length > 0)
            {
                await RefreshPlanAsync();
            }
            else
            {
                await SyncAsync();
            }
        }
        catch (Exception exception)
        {
            Ready = true;
            StorageFailed = true;
            Error = "Не удалось открыть хранилище устройства. Данные не будут перезаписаны. " + exception.Message;
            Notify();
        }
    }

    public async Task<bool> MutateAsync(Action<LocalState> mutation)
    {
        if (StorageFailed || reloadingForUpdate)
        {
            return false;
        }
        await mutationGate.WaitAsync();
        LocalState? next = null;
        try
        {
            if (reloadingForUpdate)
            {
                return false;
            }
            // Копия становится текущей только после подтверждения IndexedDB.
            next = JsonSerializer.Deserialize<LocalState>(JsonSerializer.Serialize(State))!;
            mutation(next);
            await storage.SaveAsync(next);
            State = next;
            unsavedDraft = null;
            LastSavedAt = DateTimeOffset.Now;
            Error = null;
            Notify();
            return true;
        }
        catch (JSException exception)
        {
            StorageFailed = true;
            unsavedDraft = next;
            Error = exception.Message.Contains("STALE_REVISION")
                ? "В другой вкладке появились новые данные. Перезагрузите эту вкладку перед продолжением."
                : "Не удалось сохранить на устройстве. Не закрывайте страницу; скачайте резервную копию в настройках.";
            Notify();
            return false;
        }
        catch (Exception exception)
        {
            Error = exception.Message;
            Notify();
            return false;
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public async Task RefreshPlanAsync()
    {
        if (Busy || StorageFailed || reloadingForUpdate)
        {
            return;
        }
        Busy = true;
        Error = null;
        Notify();
        try
        {
            var plan = await google.DownloadAsync(State.Settings.SpreadsheetUrl);
            await MutateAsync(state =>
            {
                state.Plan = plan;
                state.SelectedWeek = Math.Clamp(state.SelectedWeek, 1, plan.WeekCount);
                state.SelectedDay = Math.Clamp(state.SelectedDay, 0, plan.Days.Count - 1);
            });
            Notice = "План обновлён и сохранён на устройстве.";
        }
        catch (Exception exception)
        {
            Error = FriendlyError(exception);
        }
        finally
        {
            Busy = false;
            Notify();
        }
    }

    public WorkoutSession? CurrentSession
    {
        get
        {
            var plan = State.Plan;
            if (plan is null)
            {
                return null;
            }
            var day = plan.Days[State.SelectedDay];
            return State.Sessions.LastOrDefault(session => session.SpreadsheetId == plan.SpreadsheetId
                && session.SheetId == plan.SheetId && session.DayRow == day.HeaderRow
                && session.Week == State.SelectedWeek);
        }
    }

    public async Task<bool> StartAsync()
    {
        var saved = await MutateAsync(state =>
        {
            var plan = state.Plan ?? throw new InvalidOperationException("Сначала загрузите план.");
            var day = plan.Days[state.SelectedDay];
            var existing = state.Sessions.LastOrDefault(session => session.SpreadsheetId == plan.SpreadsheetId
                && session.SheetId == plan.SheetId && session.DayRow == day.HeaderRow
                && session.Week == state.SelectedWeek);
            if (existing?.FinishedAt is not null)
            {
                return;
            }
            // Одновременно активна одна тренировка. Остальные черновики сохраняются.
            foreach (var session in state.Sessions.Where(session => session.FinishedAt is null))
            {
                session.IsPaused = true;
            }
            if (existing is null)
            {
                state.Sessions.Add(WorkoutRules.Start(plan, day, state.SelectedWeek));
            }
            else
            {
                if (!existing.Exercises.Any(exercise => exercise.Sets.Any(set =>
                    set.Done || set.Weight.HasValue || set.Repetitions.HasValue || set.Reserve.HasValue
                    || set.WeightInput.Length > 0 || set.ReserveInput.Length > 0)
                    || exercise.QuickWeightInput.Length > 0 || exercise.QuickRepetitionsInput.Length > 0))
                {
                    existing.StartedAt = DateTimeOffset.UtcNow;
                }
                existing.IsPaused = false;
            }
        });
        if (saved)
        {
            await javascript.InvokeVoidAsync("workout.timer.stop");
        }
        return saved;
    }

    public async Task<bool> PauseAsync(string sessionId)
    {
        var saved = await MutateAsync(state =>
        {
            var session = state.Sessions.Single(item => item.Id == sessionId);
            session.IsPaused = true;
        });
        if (saved)
        {
            await javascript.InvokeVoidAsync("workout.timer.stop");
        }
        return saved;
    }

    public async Task<bool> SetTimerEnabledAsync(bool enabled)
    {
        var saved = await MutateAsync(state => state.Settings.TimerEnabled = enabled);
        if (saved && !enabled)
        {
            await javascript.InvokeVoidAsync("workout.timer.stop");
        }
        return saved;
    }

    public Task<bool> UpdateQuickDraftAsync(string sessionId, int row, string field, string value)
    {
        return MutateAsync(state =>
        {
            var session = state.Sessions.Single(item => item.Id == sessionId);
            EnsureEditable(session);
            var exercise = session.Exercises.Single(item => item.Row == row);
            if (field == "weight")
            {
                exercise.QuickWeightInput = value;
            }
            else
            {
                exercise.QuickRepetitionsInput = value;
            }
        });
    }

    private static void EnsureEditable(WorkoutSession session)
    {
        if (session.FinishedAt is not null || session.IsPaused)
        {
            throw new InvalidOperationException("Сначала продолжите тренировку. Завершённые записи доступны только для просмотра.");
        }
    }

    public Task<bool> UpdateSetAsync(string sessionId, int row, int index, string field, string value)
    {
        return MutateAsync(state =>
        {
            var session = state.Sessions.Single(item => item.Id == sessionId);
            EnsureEditable(session);
            var exercise = session.Exercises.Single(item => item.Row == row);
            var set = exercise.Sets[index];
            if (set.Done || exercise.Skipped)
            {
                return;
            }
            var normalized = value.Replace(',', '.').Trim();
            if (field == "weight")
            {
                set.WeightInput = value;
                set.Weight = decimal.TryParse(normalized, System.Globalization.NumberStyles.AllowDecimalPoint,
                    System.Globalization.CultureInfo.InvariantCulture, out var weight) ? weight : null;
            }
            else if (field == "reps")
            {
                set.Repetitions = int.TryParse(normalized, out var repetitions) ? repetitions : null;
            }
            else
            {
                set.ReserveInput = value;
                set.Reserve = int.TryParse(normalized, out var reserve) ? reserve : null;
            }
        });
    }

    public Task<bool> FillRemainingSetsAsync(string sessionId, int row, string weight, string repetitions)
    {
        // Все подходы сохраняются одной транзакцией IndexedDB, включая работу без сети.
        return MutateAsync(state =>
        {
            var session = state.Sessions.Single(item => item.Id == sessionId);
            WorkoutRules.FillRemainingSets(session, row, weight, repetitions);
        });
    }

    public async Task<bool> ToggleSetAsync(string sessionId, int row, int index)
    {
        return await MutateAsync(state =>
        {
            var session = state.Sessions.Single(item => item.Id == sessionId);
            EnsureEditable(session);
            var exercise = session.Exercises.Single(item => item.Row == row);
            var set = exercise.Sets[index];
            if (exercise.Skipped)
            {
                return;
            }
            if (!set.Done && !WorkoutRules.IsValid(set))
            {
                throw new InvalidOperationException("Введите вес (0–2000 кг) и повторы (1–500). Запас — от 0 до 20 или пустое поле.");
            }
            set.Done = !set.Done;
            if (set.Done && index + 1 < exercise.Sets.Count)
            {
                // Следующий подход получает удобную заготовку, но остаётся невыполненным.
                var next = exercise.Sets[index + 1];
                if (!next.Done && next.Weight is null && string.IsNullOrEmpty(next.WeightInput))
                {
                    next.Weight = set.Weight;
                    next.WeightInput = set.Weight!.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                }
                next.Repetitions ??= set.Repetitions;
                // Запас не копируем: пустое поле означает отсутствие записи пользователя.
            }
        });
    }

    public async Task<bool> CompleteAsync(string sessionId)
    {
        var saved = await MutateAsync(state =>
        {
            var session = state.Sessions.Single(item => item.Id == sessionId);
            state.Outbox.Add(WorkoutRules.Complete(session));
        });
        if (saved)
        {
            await javascript.InvokeVoidAsync("workout.timer.stop");
            Notice = "Тренировка сохранена. Результаты ожидают отправки в Google.";
            await SyncAsync();
        }
        return saved;
    }

    public async Task SyncAsync(bool manual = false)
    {
        if (!Ready || !Online || StorageFailed || reloadingForUpdate || !await syncGate.WaitAsync(0))
        {
            return;
        }
        Syncing = true;
        Notify();
        try
        {
            SignedIn = !string.IsNullOrEmpty(await javascript.InvokeAsync<string?>("workout.auth.token"));
            foreach (var item in State.Outbox.ToList())
            {
                // Конфликты всегда требуют осознанного решения, сетевые ошибки повторяем сами.
                if (item.Status == UploadStatus.Conflict
                    || (!manual && item.Status == UploadStatus.PermissionDenied)
                    || (!manual && item.RetryAfter > DateTimeOffset.UtcNow))
                {
                    continue;
                }
                if (!SignedIn)
                {
                    if (item.Status != UploadStatus.NeedsSignIn)
                    {
                        await UpdateUploadAsync(item.Id, upload =>
                        {
                            upload.Status = UploadStatus.NeedsSignIn;
                            upload.Message = "Войдите в Google в настройках. Результаты сохранены на этом устройстве.";
                        });
                    }
                    break;
                }
                try
                {
                    await google.UploadAsync(item);
                    await MutateAsync(state =>
                    {
                        state.Sessions.Single(session => session.Id == item.SessionId).SyncedAt = DateTimeOffset.UtcNow;
                        state.Outbox.RemoveAll(upload => upload.Id == item.Id);
                        // Сохраняем новый эталон для отображения последнего результата.
                        if (state.Plan?.SpreadsheetId == item.SpreadsheetId && state.Plan.SheetId == item.SheetId)
                        {
                            foreach (var cell in item.Cells)
                            {
                                var week = state.Plan.Days.SelectMany(day => day.Exercises)
                                    .SingleOrDefault(exercise => exercise.Row == cell.Row)?.Weeks
                                    .SingleOrDefault(week => week.Number == item.Week);
                                if (week is not null)
                                {
                                    week.ExistingLoad = cell.Load;
                                    if (cell.WritesReserve)
                                    {
                                        week.ExistingReserve = cell.Reserve;
                                    }
                                }
                            }
                        }
                    });
                    Notice = "Google подтвердил запись результатов.";
                }
                catch (SheetConflictException)
                {
                    await UpdateUploadAsync(item.Id, upload =>
                    {
                        upload.Status = UploadStatus.Conflict;
                        upload.Message = "В таблице изменились план или результаты. Автоматическая запись остановлена.";
                    });
                }
                catch (GoogleSignInRequiredException)
                {
                    SignedIn = false;
                    await UpdateUploadAsync(item.Id, upload =>
                    {
                        upload.Status = UploadStatus.NeedsSignIn;
                        upload.Message = "Срок входа в Google истёк. Войдите ещё раз, очередь сохранена.";
                    });
                    break;
                }
                catch (GooglePermissionException)
                {
                    await UpdateUploadAsync(item.Id, upload =>
                    {
                        upload.Status = UploadStatus.PermissionDenied;
                        upload.Message = "Нет доступа к записи. Проверьте Google-аккаунт, права редактора и включение Sheets API.";
                    });
                }
                catch (Exception)
                {
                    await UpdateUploadAsync(item.Id, upload =>
                    {
                        upload.Status = UploadStatus.Pending;
                        upload.Attempts++;
                        var delay = Math.Min(300, 10 * Math.Pow(2, Math.Min(upload.Attempts, 5)));
                        upload.RetryAfter = DateTimeOffset.UtcNow.AddSeconds(delay + Random.Shared.Next(0, 8));
                        upload.Message = "Нет подтверждения от Google. Повторим автоматически при открытом приложении.";
                    });
                }
            }
        }
        finally
        {
            Syncing = false;
            Notify();
            syncGate.Release();
        }
    }

    private Task<bool> UpdateUploadAsync(string id, Action<PendingUpload> update)
    {
        return MutateAsync(state =>
        {
            var upload = state.Outbox.SingleOrDefault(item => item.Id == id);
            if (upload is not null)
            {
                update(upload);
            }
        });
    }

    public async Task PrepareLoginAsync()
    {
        LoginReady = false;
        if (string.IsNullOrWhiteSpace(State.Settings.GoogleClientId) || !Online)
        {
            return;
        }
        try
        {
            await javascript.InvokeVoidAsync("workout.auth.prepare", State.Settings.GoogleClientId);
            LoginReady = true;
            Notify();
        }
        catch (Exception)
        {
            Error = "Не удалось загрузить вход Google. Проверьте сеть и попробуйте ещё раз.";
            Notify();
        }
    }

    public async Task SignInAsync()
    {
        try
        {
            await javascript.InvokeVoidAsync("workout.auth.signIn");
            SignedIn = true;
            Notice = "Google подключён.";
            await SyncAsync(true);
        }
        catch (Exception)
        {
            Error = "Вход не завершён. Разрешите всплывающее окно, проверьте Client ID и предоставьте доступ к таблицам.";
        }
        Notify();
    }

    public async Task SignOutAsync()
    {
        await javascript.InvokeVoidAsync("workout.auth.signOut");
        SignedIn = false;
        Notify();
    }

    public async Task ExportAsync()
    {
        await javascript.InvokeVoidAsync("workout.download",
            $"ritm-backup-{DateTime.Now:yyyy-MM-dd}.json", JsonSerializer.Serialize(unsavedDraft ?? State,
                new JsonSerializerOptions { WriteIndented = true }));
    }

    public async Task<bool> ImportAsync(string json)
    {
        var incoming = JsonSerializer.Deserialize<LocalState>(json);
        if (incoming is null || incoming.SchemaVersion != 1 || incoming.Settings is null
            || incoming.Sessions is null || incoming.Outbox is null)
        {
            throw new InvalidOperationException("Файл не является резервной копией РИТМ.");
        }
        // Импорт объединяет записи по ID и не удаляет существующие результаты.
        return await MutateAsync(state =>
        {
            foreach (var session in incoming.Sessions)
            {
                if (state.Sessions.All(existing => existing.Id != session.Id))
                {
                    state.Sessions.Add(session);
                    var pending = incoming.Outbox.SingleOrDefault(item => item.SessionId == session.Id);
                    if (pending is not null && session.SyncedAt is null)
                    {
                        state.Outbox.Add(pending);
                    }
                }
            }
            // Параметры новых упражнений переносим вместе с историей.
            // Уже настроенные на этом устройстве значения не перезаписываем.
            foreach (var (key, settings) in incoming.ProgressionSettings ?? [])
            {
                state.ProgressionSettings.TryAdd(key, settings);
            }
            state.Plan ??= incoming.Plan;
        });
    }

    public async Task RequestPersistenceAsync()
    {
        PersistentStorage = await javascript.InvokeAsync<bool>("workout.storage.persist");
        Notice = PersistentStorage
            ? "Браузер разрешил постоянное хранение."
            : "Браузер пока не разрешил постоянное хранение. Сохраняйте резервную копию.";
        Notify();
    }

    [JSInvokable]
    public void AppUpdateReady()
    {
        UpdateAvailable = true;
        Notify();
    }

    [JSInvokable]
    public async Task<bool> PrepareAppReload()
    {
        // Ждём окончания записи. Во время тренировки, синхронизации и ошибки хранения
        // автоматическая перезагрузка запрещена: пользователь продолжает работу.
        await mutationGate.WaitAsync();
        try
        {
            if (!Ready || StorageFailed || HasActiveWorkout || Busy || Syncing)
            {
                return false;
            }
            reloadingForUpdate = true;
            return true;
        }
        finally
        {
            mutationGate.Release();
        }
    }

    [JSInvokable]
    public async Task BrowserChanged(bool online)
    {
        Online = online;
        OfflineReady = await javascript.InvokeAsync<bool>("workout.pwa.ready");
        Notify();
        if (online)
        {
            await SyncAsync();
        }
    }

    public void DismissMessage()
    {
        Error = null;
        Notice = null;
        Notify();
    }

    public void ReportError(string message)
    {
        Error = message;
        Notify();
    }

    private static string FriendlyError(Exception exception)
    {
        return exception switch
        {
            GoogleSignInRequiredException => "Войдите в Google в настройках, чтобы открыть этот план.",
            GooglePermissionException => "Нет доступа к таблице. Проверьте аккаунт, права доступа и Sheets API.",
            HttpRequestException or TaskCanceledException =>
                "Не удалось загрузить обновление. Последний сохранённый план остаётся доступен.",
            _ => exception.Message
        };
    }

    private void Notify()
    {
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await javascript.InvokeVoidAsync("workout.dispose");
        reference?.Dispose();
        mutationGate.Dispose();
        syncGate.Dispose();
    }
}
