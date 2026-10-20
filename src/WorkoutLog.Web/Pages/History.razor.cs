using Microsoft.AspNetCore.Components;
using WorkoutLog.Core;
using WorkoutLog.Web.Services;

namespace WorkoutLog.Web.Pages;

public partial class History
{
    [Inject] private AppStore Store { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    private int CompletedCount => Store.State.Sessions.Count(session => session.FinishedAt is not null);

    protected override async Task OnInitializedAsync()
    {
        Store.Changed += OnStoreChanged;
        await Store.InitializeAsync();
    }

    private Task SyncAsync()
    {
        return Store.SyncAsync(true);
    }

    private async Task RetryConflictAsync(string id)
    {
        await Store.MutateAsync(state =>
        {
            var upload = state.Outbox.Single(item => item.Id == id);
            upload.Status = UploadStatus.Pending;
            upload.RetryAfter = DateTimeOffset.MinValue;
        });
        await Store.SyncAsync(true);
    }

    private bool CanResume(WorkoutSession session)
    {
        return Store.State.Plan is { } plan && plan.SpreadsheetId == session.SpreadsheetId
            && plan.SheetId == session.SheetId && plan.Days.Any(day => day.HeaderRow == session.DayRow);
    }

    private async Task ResumeAsync(WorkoutSession session)
    {
        await Store.MutateAsync(state =>
        {
            state.SelectedWeek = session.Week;
            state.SelectedDay = state.Plan!.Days.FindIndex(day => day.HeaderRow == session.DayRow);
        });
        Navigation.NavigateTo("");
    }

    private static string TableUrl(PendingUpload upload)
    {
        return $"https://docs.google.com/spreadsheets/d/{upload.SpreadsheetId}/edit?gid={upload.SheetId}";
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
