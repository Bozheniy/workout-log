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

    private HashSet<int> ExpandedRows { get; } = [];
    private bool scrollToFirstExercise;
    private WorkoutPlan? Plan => Store.State.Plan;
    private TrainingDay? CurrentDay => Plan?.Days[Store.State.SelectedDay];
    private WorkoutSession? Session => Store.CurrentSession;
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
        ExpandedRows.Clear();
        await Store.MutateAsync(state => state.SelectedWeek = week);
        ExpandFirstExercise();
    }

    private async Task SelectDayAsync(int day)
    {
        ExpandedRows.Clear();
        await Store.MutateAsync(state => state.SelectedDay = day);
        ExpandFirstExercise();
    }

    private async Task StartAsync()
    {
        await Store.StartAsync();
        ExpandFirstExercise();
        scrollToFirstExercise = true;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (scrollToFirstExercise && ExpandedRows.Count > 0)
        {
            scrollToFirstExercise = false;
            await Javascript.InvokeVoidAsync("workout.scrollToExercise", ExpandedRows.First());
        }
    }

    private void ExpandFirstExercise()
    {
        var first = Session?.Exercises.FirstOrDefault(exercise => !exercise.Sets.All(set => set.Done));
        if (first is not null)
        {
            ExpandedRows.Add(first.Row);
        }
    }

    private bool IsDayCompleted(TrainingDay day)
    {
        return Store.State.Sessions.Any(session => session.SpreadsheetId == Plan?.SpreadsheetId
            && session.SheetId == Plan.SheetId && session.DayRow == day.HeaderRow
            && session.Week == Store.State.SelectedWeek && session.FinishedAt is not null);
    }

    private void ToggleExpanded(int row)
    {
        if (!ExpandedRows.Add(row))
        {
            ExpandedRows.Remove(row);
        }
    }

    private async Task UpdateSetAsync(int row, int index, string field, ChangeEventArgs arguments)
    {
        if (Session is not null)
        {
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
        if (await Store.ToggleSetAsync(Session.Id, row, index) && !wasDone)
        {
            await StartTimerAsync(Store.State.Settings.RestSeconds);
        }
    }

    private Task<bool> SkipAsync(int row)
    {
        var sessionId = Session!.Id;
        return Store.MutateAsync(state =>
        {
            var session = state.Sessions.Single(item => item.Id == sessionId);
            if (session.FinishedAt is null)
            {
                var exercise = session.Exercises.Single(item => item.Row == row);
                exercise.Skipped = !exercise.Skipped;
            }
        });
    }

    private Task FinishAsync()
    {
        return Store.CompleteAsync(Session!.Id);
    }

    private async Task StartTimerAsync(int seconds)
    {
        await Javascript.InvokeVoidAsync("workout.timer.start", seconds);
    }

    private async Task StopTimerAsync()
    {
        await Javascript.InvokeVoidAsync("workout.timer.stop");
    }

    private static string FormatWeight(decimal? weight)
    {
        return weight?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
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
