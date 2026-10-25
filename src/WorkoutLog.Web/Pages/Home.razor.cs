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
    private WorkoutPlan? Plan => Store.State.Plan;
    private TrainingDay? CurrentDay => Plan?.Days[Store.State.SelectedDay];
    private WorkoutSession? Session => Store.CurrentSession;
    private bool IsActive => Session is { FinishedAt: null, IsPaused: false };
    private bool ShowTimer => IsActive && Store.State.Settings.TimerEnabled;
    private string? FinishProblem => Session is null ? null : WorkoutRules.CompletionProblem(Session);
    private List<PlannedExercise> PlannedExercises => CurrentDay?.Exercises
        .Where(exercise => exercise.Weeks.Any(week => week.Number == Store.State.SelectedWeek)).ToList() ?? [];
    private int ExerciseCount => Session?.Exercises.Sum(exercise => exercise.Entries.Count())
        ?? PlannedExercises.Sum(exercise => Math.Max(1, SupersetRules.Names(exercise.Name).Count));
    private int TotalSets => Session?.TotalSets ?? PlannedExercises.Sum(exercise =>
        exercise.Weeks.Single(week => week.Number == Store.State.SelectedWeek).Sets
            * Math.Max(1, SupersetRules.Names(exercise.Name).Count));
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
            .FirstOrDefault(exercise => !exercise.IsComplete)?.Row;
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

    private async Task PauseAsync()
    {
        if (await Store.PauseAsync(Session!.Id))
        {
            ExpandedRow = null;
            FinishError = null;
        }
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

    private ProgressionContext ProgressionFor(int row, string name, int sets, string repetitions, string? partName = null)
    {
        return new ProgressionContext(Plan!.SpreadsheetId, Plan.SheetId,
            CurrentDay!.HeaderRow, row, name, Store.State.SelectedWeek,
            sets, repetitions, Session?.StartedAt, partName);
    }

    private void ClearFinishError()
    {
        FinishError = null;
    }

    private Task<bool> SplitSupersetAsync(int row)
    {
        var sessionId = Session!.Id;
        return Store.MutateAsync(state => SupersetRules.Enable(
            state.Sessions.Single(session => session.Id == sessionId), row));
    }

    private static string ExerciseSummary(ExerciseResult exercise)
    {
        return exercise.Parts.Count > 0
            ? $"Суперсет · {exercise.Sets.Count} круга · {exercise.Parts.Count} упражнения"
            : $"{exercise.Sets.Count} подхода × {exercise.PlannedRepetitions} повторений";
    }

    private static IEnumerable<(string? Name, string Repetitions)> PreviewParts(PlannedExercise exercise, WeekPrescription week)
    {
        var names = SupersetRules.Names(exercise.Name);
        return names.Count == 0
            ? [(null, week.Repetitions)]
            : names.Select((name, index) => ((string?)name, SupersetRules.Repetitions(week.Repetitions, index, names.Count)));
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
