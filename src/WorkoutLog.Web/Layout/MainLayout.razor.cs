using System.Globalization;
using Microsoft.AspNetCore.Components;
using WorkoutLog.Web.Services;

namespace WorkoutLog.Web.Layout;

public partial class MainLayout
{
    [Inject] private AppStore Store { get; set; } = default!;
    private string TodayLabel => DateTime.Now.ToString("d MMMM, ddd", CultureInfo.GetCultureInfo("ru-RU"));

    protected override async Task OnInitializedAsync()
    {
        Store.Changed += OnStoreChanged;
        await Store.InitializeAsync();
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
