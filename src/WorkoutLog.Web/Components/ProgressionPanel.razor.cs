using System.Globalization;
using Microsoft.AspNetCore.Components;
using WorkoutLog.Core;
using WorkoutLog.Web.Services;

namespace WorkoutLog.Web.Components;

public partial class ProgressionPanel
{
    [Inject] private AppStore Store { get; set; } = default!;
    [Parameter, EditorRequired] public ProgressionContext Context { get; set; } = default!;

    private PreviousExercise? Previous { get; set; }
    private ProgressionAdvice Advice { get; set; } = new();
    private ExerciseProgressionSettings Settings { get; set; } = new();
    private string? SettingsError { get; set; }
    private string? DraftKey { get; set; }
    private string StepInput { get; set; } = "";
    private string FirstReserveInput { get; set; } = "";
    private string LastReserveInput { get; set; } = "";
    private bool SettingsSaved { get; set; }
    private int ComparisonRows => Math.Max(Context.SetCount,
        Previous?.Sets.Select(set => set.Number).DefaultIfEmpty(0).Max() ?? 0);

    protected override void OnParametersSet()
    {
        Recalculate();
        if (DraftKey != Context.SettingsKey)
        {
            DraftKey = Context.SettingsKey;
            StepInput = Weight(Settings.WeightStep);
            FirstReserveInput = Settings.FirstSetReserve.ToString();
            LastReserveInput = Settings.LastSetReserve.ToString();
            SettingsSaved = false;
            SettingsError = null;
        }
    }

    private void Recalculate()
    {
        Settings = Store.State.ProgressionSettings.GetValueOrDefault(Context.SettingsKey) ?? new();
        Previous = PreviousWorkoutFinder.Find(Store.State, Context);
        Advice = ProgressionCalculator.Calculate(Context, Previous, Settings);
    }

    private async Task SaveSettingsAsync()
    {
        SettingsSaved = false;
        var text = StepInput.Trim().Replace(',', '.');
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var step)
            || step is <= 0 or > 100 || decimal.Round(step, 2) != step
            || !int.TryParse(FirstReserveInput, out var first) || first is < 0 or > 20
            || !int.TryParse(LastReserveInput, out var last) || last is < 0 or > 20)
        {
            SettingsError = "Шаг: от 0,01 до 100 кг, до двух знаков после запятой. Запас: целое число от 0 до 20.";
            return;
        }
        SettingsError = null;
        var saved = await Store.MutateAsync(state =>
        {
            // Сохраняем все три поля одной операцией, чтобы расчёт не видел полунастроенную схему.
            state.ProgressionSettings[Context.SettingsKey] = new ExerciseProgressionSettings
            {
                WeightStep = step,
                FirstSetReserve = first,
                LastSetReserve = last
            };
        });
        if (!saved)
        {
            SettingsError = "Не удалось сохранить параметры. Проверь сообщение о хранилище.";
        }
        SettingsSaved = saved;
        Recalculate();
    }

    private void OnDraftChanged()
    {
        SettingsSaved = false;
        SettingsError = null;
    }

    private static string Weight(decimal value)
    {
        return value.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU"));
    }
}
