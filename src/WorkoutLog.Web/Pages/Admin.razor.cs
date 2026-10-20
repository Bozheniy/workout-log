using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using WorkoutLog.Core;
using WorkoutLog.Web.Services;

namespace WorkoutLog.Web.Pages;

// Разметка находится в Admin.razor. Здесь — только поведение экрана настроек.
public partial class Admin
{
    [Inject] private AppStore Store { get; set; } = default!;
    [Inject] private IJSRuntime Javascript { get; set; } = default!;

    private string SpreadsheetUrl { get; set; } = "";
    private string ClientId { get; set; } = "";
    private int RestSeconds { get; set; } = 120;
    private string? InstallHint { get; set; }

    protected override async Task OnInitializedAsync()
    {
        Store.Changed += OnStoreChanged;
        await Store.InitializeAsync();
        SpreadsheetUrl = Store.State.Settings.SpreadsheetUrl;
        ClientId = Store.State.Settings.GoogleClientId;
        RestSeconds = Store.State.Settings.RestSeconds;
        await Store.PrepareLoginAsync();
    }

    private async Task SaveSourceAsync()
    {
        try
        {
            SheetParser.ParseUrl(SpreadsheetUrl);
            if (await Store.MutateAsync(state => state.Settings.SpreadsheetUrl = SpreadsheetUrl.Trim()))
            {
                await Store.RefreshPlanAsync();
            }
        }
        catch (Exception exception)
        {
            Store.ReportError(exception.Message);
        }
    }

    private async Task SaveClientAsync()
    {
        ClientId = ClientId.Trim();
        if (!ClientId.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal) || ClientId.Contains(' '))
        {
            Store.ReportError("Client ID должен заканчиваться на .apps.googleusercontent.com.");
            return;
        }
        if (await Store.MutateAsync(state => state.Settings.GoogleClientId = ClientId))
        {
            await Store.SignOutAsync();
            await Store.PrepareLoginAsync();
        }
    }

    private Task<bool> SaveRestAsync()
    {
        return Store.MutateAsync(state => state.Settings.RestSeconds = RestSeconds);
    }

    private async Task InstallAsync()
    {
        var installed = await Javascript.InvokeAsync<bool>("workout.pwa.install");
        InstallHint = installed
            ? "Готово! Иконка появится на главном экране."
            : "iPhone: Safari → «Поделиться» → «На экран Домой». Android: меню Chrome → «Установить приложение» или «Добавить на главный экран».";
    }

    private async Task ImportAsync(InputFileChangeEventArgs arguments)
    {
        try
        {
            using var reader = new StreamReader(arguments.File.OpenReadStream(10 * 1024 * 1024));
            await Store.ImportAsync(await reader.ReadToEndAsync());
        }
        catch (Exception exception)
        {
            Store.ReportError("Не удалось импортировать копию: " + exception.Message);
        }
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
