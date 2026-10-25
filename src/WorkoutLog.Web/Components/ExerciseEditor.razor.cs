using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using WorkoutLog.Core;
using WorkoutLog.Web.Services;

namespace WorkoutLog.Web.Components;

// Один редактор для обычного упражнения и каждой части суперсета.
public partial class ExerciseEditor
{
    [Inject] private AppStore Store { get; set; } = default!;
    [Inject] private IJSRuntime Javascript { get; set; } = default!;
    [Parameter, EditorRequired] public string SessionId { get; set; } = "";
    [Parameter] public int ExerciseRow { get; set; }
    [Parameter, EditorRequired] public ExercisePerformance Entry { get; set; } = default!;
    [Parameter, EditorRequired] public ProgressionContext Context { get; set; } = default!;
    [Parameter] public int? PartIndex { get; set; }
    [Parameter] public bool IsActive { get; set; }
    [Parameter] public EventCallback OnChanged { get; set; }

    private string? ExerciseError { get; set; }
    private string TechniqueId => $"technique-{ExerciseRow}-{PartIndex}";
    private string TechniqueValue => Entry.TechniqueMaintained switch
    {
        true => "yes",
        false => "no",
        _ => ""
    };

    private async Task UpdateQuickDraftAsync(string field, ChangeEventArgs arguments)
    {
        ExerciseError = null;
        await Store.UpdateQuickDraftAsync(SessionId, ExerciseRow, field, arguments.Value?.ToString() ?? "", PartIndex);
        await OnChanged.InvokeAsync();
    }

    private async Task ApplyQuickDraftAsync()
    {
        var saved = await Store.FillRemainingSetsAsync(SessionId, ExerciseRow,
            Entry.QuickWeightInput, Entry.QuickRepetitionsInput, PartIndex);
        ExerciseError = saved ? null : Store.Error;
        await OnChanged.InvokeAsync();
        if (saved)
        {
            await StartRestAsync(null);
        }
    }

    private async Task UpdateSetAsync(int index, string field, ChangeEventArgs arguments)
    {
        ExerciseError = null;
        await Store.UpdateSetAsync(SessionId, ExerciseRow, index, field, arguments.Value?.ToString() ?? "", PartIndex);
        await OnChanged.InvokeAsync();
    }

    private async Task ToggleSetAsync(int index)
    {
        var wasDone = Entry.Sets[index].Done;
        var saved = await Store.ToggleSetAsync(SessionId, ExerciseRow, index, PartIndex);
        ExerciseError = saved ? null : Store.Error;
        await OnChanged.InvokeAsync();
        if (saved && !wasDone)
        {
            await StartRestAsync(index);
        }
    }

    private async Task StartRestAsync(int? index)
    {
        if (!IsActive || !Store.State.Settings.TimerEnabled)
        {
            return;
        }
        var exercise = Store.State.Sessions.Single(session => session.Id == SessionId)
            .Exercises.Single(item => item.Row == ExerciseRow);
        // В суперсете отдых начинается после обеих частей круга, а не между ними.
        var roundDone = index is null ? exercise.IsComplete
            : exercise.Entries.All(entry => entry.Skipped || entry.Sets[index.Value].Done);
        if (PartIndex is null || roundDone)
        {
            await Javascript.InvokeVoidAsync("workout.timer.start", Store.State.Settings.RestSeconds);
        }
    }

    private async Task SkipAsync()
    {
        await EditEntryAsync(entry => entry.Skipped = !entry.Skipped);
    }

    private Task SaveTechniqueAsync(ChangeEventArgs arguments)
    {
        var value = arguments.Value?.ToString();
        return EditEntryAsync(entry => entry.TechniqueMaintained = value == "yes" ? true : value == "no" ? false : null);
    }

    private async Task EditEntryAsync(Action<ExercisePerformance> change)
    {
        var saved = await Store.MutateAsync(state =>
        {
            var session = state.Sessions.Single(item => item.Id == SessionId);
            if (session.FinishedAt is not null || session.IsPaused)
            {
                throw new InvalidOperationException("Сначала продолжите тренировку.");
            }
            change(session.Exercises.Single(exercise => exercise.Row == ExerciseRow).Entry(PartIndex));
        });
        ExerciseError = saved ? null : Store.Error;
        await OnChanged.InvokeAsync();
    }

    private static string FormatWeight(decimal? weight)
    {
        return weight?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
    }
}
