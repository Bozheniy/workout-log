using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using WorkoutLog.Core;
using WorkoutLog.Web.Services;

namespace WorkoutLog.Web.Pages;

public partial class Home
{
    [Inject] private AppStore Store { get; set; } = default!;
    [Inject] private IJSRuntime Javascript { get; set; } = default!;

    private int? ExpandedRow { get; set; }
    private bool scrollToExpandedExercise;
    private string? FinishError { get; set; }
    private bool Finishing { get; set; }
    private int? ErrorRow { get; set; }
    private string? ExerciseError { get; set; }
    private WorkoutPlan? Plan => Store.State.Plan;
    private TrainingDay? CurrentDay => Plan?.Days[Store.State.SelectedDay];
    private WorkoutSession? Session => Store.CurrentSession;
    private bool IsActive => Session is { FinishedAt: null, IsPaused: false };
    private bool ShowTimer => IsActive && Store.State.Settings.TimerEnabled;
    private string? FinishProblem => Session is null ? null : WorkoutRules.CompletionProblem(Session);
    private List<PlannedExercise> PlannedExercises => CurrentDay?.Exercises
        .Where(exercise => exercise.Weeks.Any(week => week.Number == Store.State.SelectedWeek)).ToList() ?? [];
    private int ExerciseCount => Session?.Exercises.Count ?? PlannedExercises.Count;
    private int TotalSets => Session?.TotalSets ?? PlannedExercises.Sum(exercise =>
        exercise.Weeks.Single(week => week.Number == Store.State.SelectedWeek).Sets);
    private int ProgressPercent => TotalSets == 0 ? 0 : (Session?.CompletedSets ?? 0) * 100 / TotalSets;

    protected override async Task OnInitializedAsync()
    {
        Store.Changed += OnStoreChanged;
        await Store.InitializeAsync();
        ExpandFirstExercise();
    }

    private async Task SelectWeekAsync(int week)
    {
        FinishError = null;
        ExpandedRow = null;
        await Store.MutateAsync(state => state.SelectedWeek = week);
        ExpandFirstExercise();
    }

    private async Task SelectDayAsync(int day)
    {
        FinishError = null;
        ExpandedRow = null;
        await Store.MutateAsync(state => state.SelectedDay = day);
        ExpandFirstExercise();
    }

    private async Task StartAsync()
    {
        if (!await Store.StartAsync())
        {
            return;
        }
        await StopTimerAsync();
        FinishError = null;
        ExpandFirstExercise();
        scrollToExpandedExercise = true;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (scrollToExpandedExercise && ExpandedRow is not null)
        {
            scrollToExpandedExercise = false;
            await Javascript.InvokeVoidAsync("workout.scrollToExercise", ExpandedRow.Value);
        }
    }

    private void ExpandFirstExercise()
    {
        ExpandedRow = !IsActive ? null : Session?.Exercises
            .FirstOrDefault(exercise => !exercise.Skipped && !exercise.Sets.All(set => set.Done))?.Row;
    }

    private bool IsDayCompleted(TrainingDay day)
    {
        return Store.State.Sessions.Any(session => session.SpreadsheetId == Plan?.SpreadsheetId
            && session.SheetId == Plan.SheetId && session.DayRow == day.HeaderRow
            && session.Week == Store.State.SelectedWeek && session.FinishedAt is not null);
    }

    private void ToggleExpanded(int row)
    {
        // Открытой может быть только одна карточка; повторное нажатие закрывает её.
        ExpandedRow = ExpandedRow == row ? null : row;
        scrollToExpandedExercise = ExpandedRow is not null;
    }

    private void OpenExercise(int row)
    {
        ExpandedRow = row;
        scrollToExpandedExercise = true;
    }

    private async Task UpdateQuickDraftAsync(int row, string field, ChangeEventArgs arguments)
    {
        ExerciseError = null;
        await Store.UpdateQuickDraftAsync(Session!.Id, row, field, arguments.Value?.ToString() ?? "");
    }

    private async Task ApplyQuickDraftAsync(ExerciseResult exercise)
    {
        var saved = await Store.FillRemainingSetsAsync(Session!.Id, exercise.Row,
            exercise.QuickWeightInput, exercise.QuickRepetitionsInput);
        ErrorRow = exercise.Row;
        ExerciseError = saved ? null : Store.Error;
        FinishError = null;
        if (saved)
        {
            await StartTimerAsync(Store.State.Settings.RestSeconds);
        }
    }

    private async Task PauseAsync()
    {
        if (await Store.PauseAsync(Session!.Id))
        {
            ExpandedRow = null;
            FinishError = null;
            ExerciseError = null;
        }
    }

    private async Task UpdateSetAsync(int row, int index, string field, ChangeEventArgs arguments)
    {
        if (Session is not null)
        {
            FinishError = null;
            ExerciseError = null;
            await Store.UpdateSetAsync(Session.Id, row, index, field, arguments.Value?.ToString() ?? "");
        }
    }

    private async Task ToggleSetAsync(int row, int index)
    {
        if (Session is null)
        {
            return;
        }
        var wasDone = Session.Exercises.Single(exercise => exercise.Row == row).Sets[index].Done;
        var saved = await Store.ToggleSetAsync(Session.Id, row, index);
        ErrorRow = row;
        ExerciseError = saved ? null : Store.Error;
        FinishError = null;
        if (saved && !wasDone)
        {
            await StartTimerAsync(Store.State.Settings.RestSeconds);
        }
    }

    private Task<bool> SkipAsync(int row)
    {
        FinishError = null;
        var sessionId = Session!.Id;
        return Store.MutateAsync(state =>
        {
            var session = state.Sessions.Single(item => item.Id == sessionId);
            if (session.FinishedAt is null && !session.IsPaused)
            {
                var exercise = session.Exercises.Single(item => item.Row == row);
                exercise.Skipped = !exercise.Skipped;
            }
        });
    }

    private async Task FinishAsync()
    {
        if (Finishing)
        {
            return;
        }
        FinishError = FinishProblem;
        if (FinishError is not null)
        {
            return;
        }
        Finishing = true;
        try
        {
            if (!await Store.CompleteAsync(Session!.Id))
            {
                FinishError = Store.Error ?? "Не удалось сохранить. Попробуйте ещё раз.";
            }
        }
        finally
        {
            Finishing = false;
        }
    }

    private async Task StartTimerAsync(int seconds)
    {
        if (ShowTimer)
        {
            await Javascript.InvokeVoidAsync("workout.timer.start", seconds);
        }
    }

    private async Task StopTimerAsync()
    {
        await Javascript.InvokeVoidAsync("workout.timer.stop");
    }

    private static string FormatWeight(decimal? weight)
    {
        return weight?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
    }

    private ProgressionContext ProgressionFor(int row, string name, int sets, string repetitions)
    {
        return new ProgressionContext(Plan!.SpreadsheetId, Plan.SheetId,
            CurrentDay!.HeaderRow, row, name, Store.State.SelectedWeek,
            sets, repetitions, Session?.StartedAt);
    }

    private Task<bool> SaveTechniqueAsync(int row, ChangeEventArgs arguments)
    {
        var sessionId = Session!.Id;
        var value = arguments.Value?.ToString();
        return Store.MutateAsync(state =>
        {
            var session = state.Sessions.Single(item => item.Id == sessionId);
            if (session.FinishedAt is null && !session.IsPaused)
            {
                var exercise = session.Exercises.Single(item => item.Row == row);
                exercise.TechniqueMaintained = value == "yes" ? true : value == "no" ? false : null;
            }
        });
    }

    private static string TechniqueValue(ExerciseResult exercise)
    {
        return exercise.TechniqueMaintained switch
        {
            true => "yes",
            false => "no",
            _ => ""
        };
    }

    private void OnStoreChanged()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        Store.Changed -= OnStoreChanged;
    }
}
