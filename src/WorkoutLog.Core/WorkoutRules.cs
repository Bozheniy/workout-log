using System.Globalization;

namespace WorkoutLog.Core;

public static class WorkoutRules
{
    public static WorkoutSession Start(WorkoutPlan plan, TrainingDay day, int week)
    {
        var session = new WorkoutSession
        {
            SpreadsheetId = plan.SpreadsheetId,
            SheetId = plan.SheetId,
            DayRow = day.HeaderRow,
            DayName = day.Name,
            Week = week,
            IsPaused = false
        };

        foreach (var exercise in day.Exercises)
        {
            var prescription = exercise.Weeks.SingleOrDefault(item => item.Number == week);
            if (prescription is null)
            {
                continue;
            }
            session.Exercises.Add(new ExerciseResult
            {
                Row = exercise.Row,
                Name = exercise.Name,
                LoadColumn = prescription.LoadColumn,
                PlannedRepetitions = prescription.Repetitions,
                BaselineLoad = prescription.ExistingLoad,
                BaselineReserve = prescription.ExistingReserve,
                Sets = Enumerable.Range(0, prescription.Sets).Select(_ => new SetResult()).ToList()
            });
        }
        SupersetRules.PrepareEmptyEntries(session);
        return session;
    }

    public static bool IsValid(SetResult set)
    {
        return set.Weight is >= 0 and <= 2000
            && set.Repetitions is >= 1 and <= 500
            && IsReserveValid(set);
    }

    public static bool IsReserveValid(SetResult set)
    {
        return set.Reserve is >= 0 and <= 20
            || (set.Reserve is null && string.IsNullOrWhiteSpace(set.ReserveInput));
    }

    public static string FormatReserves(IEnumerable<SetResult> sets)
    {
        var completed = sets.ToList();
        // Прочерк сохраняет номер подхода, если заполнен запас только части подходов.
        return completed.Any(set => set.Reserve.HasValue)
            ? string.Join(" / ", completed.Select(set => set.Reserve?.ToString(CultureInfo.InvariantCulture) ?? "—"))
            : "";
    }

    public static void FillRemainingSets(WorkoutSession session, int exerciseRow, string weightInput, string repetitionsInput,
        int? partIndex = null)
    {
        if (session.FinishedAt is not null)
        {
            throw new InvalidOperationException("Завершённая тренировка доступна для просмотра.");
        }
        if (session.IsPaused)
        {
            throw new InvalidOperationException("Сначала продолжите тренировку.");
        }
        var exercise = session.Exercises.Single(item => item.Row == exerciseRow).Entry(partIndex);
        if (exercise.Skipped)
        {
            throw new InvalidOperationException("Сначала верните пропущенное упражнение.");
        }

        var normalizedWeight = weightInput.Trim().Replace(',', '.');
        if (!decimal.TryParse(normalizedWeight, NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var weight) || weight is < 0 or > 2000
            || !int.TryParse(repetitionsInput.Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out var repetitions) || repetitions is < 1 or > 500)
        {
            throw new InvalidOperationException("Введите вес от 0 до 2000 кг и повторы от 1 до 500.");
        }
        var remaining = exercise.Sets.Where(set => !set.Done).ToList();
        if (remaining.Count == 0)
        {
            throw new InvalidOperationException("Все подходы уже выполнены.");
        }
        if (remaining.Any(set => !IsReserveValid(set)))
        {
            throw new InvalidOperationException("Запас можно оставить пустым или указать число от 0 до 20.");
        }
        // «Применить» подтверждает все оставшиеся подходы одним сохранением.
        foreach (var set in remaining)
        {
            set.Weight = weight;
            set.WeightInput = weightInput.Trim();
            set.Repetitions = repetitions;
            set.Done = true;
        }
        exercise.QuickWeightInput = weightInput.Trim();
        exercise.QuickRepetitionsInput = repetitionsInput.Trim();
    }

    public static string? CompletionProblem(WorkoutSession session)
    {
        if (session.FinishedAt is not null)
        {
            return "Тренировка уже завершена.";
        }
        if (session.IsPaused)
        {
            return "Сначала продолжите тренировку.";
        }
        if (session.CompletedSets == 0)
        {
            return "Пока нет выполненных подходов. Нажмите «Применить» в упражнении или отметьте подход галочкой.";
        }
        var unfinished = session.Exercises.SelectMany(exercise => exercise.Entries)
            .Where(exercise => !exercise.Skipped && exercise.Sets.Any(set => !set.Done)).ToList();
        if (unfinished.Count > 0)
        {
            return $"Остались незавершённые упражнения: {unfinished.Count}. Выполните их или пропустите остаток.";
        }
        if (session.Exercises.SelectMany(exercise => exercise.Entries).SelectMany(entry => entry.Sets)
            .Any(set => set.Done && !IsValid(set)))
        {
            return "Проверьте вес и повторы выполненных подходов. Запас — от 0 до 20 или пустое поле.";
        }
        return null;
    }

    public static PendingUpload Complete(WorkoutSession session)
    {
        var problem = CompletionProblem(session);
        if (problem is not null)
        {
            throw new InvalidOperationException(problem);
        }

        var upload = new PendingUpload
        {
            SessionId = session.Id,
            SpreadsheetId = session.SpreadsheetId,
            SheetId = session.SheetId,
            Week = session.Week,
            DayRow = session.DayRow,
            DayName = session.DayName
        };

        foreach (var exercise in session.Exercises)
        {
            // Полностью пропущенное упражнение оставляет таблицу без изменений.
            if (exercise.CompletedSets == 0)
            {
                continue;
            }

            upload.Cells.Add(new CellUpdate
            {
                Row = exercise.Row,
                LoadColumn = exercise.LoadColumn,
                ExerciseName = exercise.Name,
                PlannedSets = exercise.Sets.Count,
                PlannedRepetitions = exercise.PlannedRepetitions,
                ExpectedLoad = exercise.BaselineLoad,
                ExpectedReserve = exercise.BaselineReserve,
                Load = ExerciseCellFormat.Load(exercise),
                Reserve = ExerciseCellFormat.Reserve(exercise)
            });
        }
        session.FinishedAt = DateTimeOffset.UtcNow;
        return upload;
    }
}

public enum ReconciliationResult
{
    SafeToWrite,
    AlreadyApplied,
    Conflict
}

public static class UploadReconciliation
{
    public static ReconciliationResult Evaluate(PendingUpload upload, WorkoutPlan remote)
    {
        if (upload.SpreadsheetId != remote.SpreadsheetId || upload.SheetId != remote.SheetId)
        {
            return ReconciliationResult.Conflict;
        }
        var day = remote.Days.SingleOrDefault(item =>
            item.HeaderRow == upload.DayRow && item.Name == upload.DayName);
        if (day is null)
        {
            return ReconciliationResult.Conflict;
        }

        var allApplied = true;
        foreach (var cell in upload.Cells)
        {
            var exercise = day.Exercises.SingleOrDefault(item =>
                item.Row == cell.Row && item.Name == cell.ExerciseName);
            var week = exercise?.Weeks.SingleOrDefault(item => item.Number == upload.Week);
            if (week is null || week.LoadColumn != cell.LoadColumn
                || week.Sets != cell.PlannedSets || week.Repetitions != cell.PlannedRepetitions)
            {
                return ReconciliationResult.Conflict;
            }

            var applied = week.ExistingLoad == cell.Load
                && (!cell.WritesReserve || week.ExistingReserve == cell.Reserve);
            var unchanged = week.ExistingLoad == cell.ExpectedLoad
                && (!cell.WritesReserve || week.ExistingReserve == cell.ExpectedReserve);
            if (week.ExistingLoad.StartsWith('=') || (cell.WritesReserve && week.ExistingReserve.StartsWith('=')))
            {
                return ReconciliationResult.Conflict;
            }
            if (!applied && !unchanged)
            {
                return ReconciliationResult.Conflict;
            }
            allApplied &= applied;
        }

        // Если ответ на запись потерялся, повторное чтение узнает уже записанный результат.
        // Мы обновляем фиксированные ячейки, а не добавляем дублирующиеся строки.
        return allApplied ? ReconciliationResult.AlreadyApplied : ReconciliationResult.SafeToWrite;
    }
}
