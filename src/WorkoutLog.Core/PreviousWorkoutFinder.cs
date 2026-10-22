using System.Globalization;
using System.Text.RegularExpressions;

namespace WorkoutLog.Core;

// Источники истории: завершённые записи устройства и уже загруженные ячейки Google.
// Для расчёта сеть не нужна. Текущая и будущие недели никогда не считаются прошлыми.
public static partial class PreviousWorkoutFinder
{
    public static PreviousExercise? Find(LocalState state, ProgressionContext context)
    {
        var exercise = state.Plan?.Days
            .SingleOrDefault(day => day.HeaderRow == context.DayRow)?.Exercises
            .SingleOrDefault(item => item.Row == context.ExerciseRow && item.Name == context.ExerciseName);
        if (state.Plan?.SpreadsheetId != context.SpreadsheetId || state.Plan.SheetId != context.SheetId)
        {
            exercise = null;
        }

        for (var week = context.Week - 1; week >= 1; week--)
        {
            var session = state.Sessions
                .Where(item => item.SpreadsheetId == context.SpreadsheetId
                    && item.SheetId == context.SheetId
                    && item.DayRow == context.DayRow && item.Week == week
                    && item.FinishedAt is not null
                    && (context.StartedAt is null || item.FinishedAt <= context.StartedAt))
                .Where(item => item.Exercises.Any(result =>
                    result.Row == context.ExerciseRow && result.Name == context.ExerciseName
                    && result.Sets.Any(set => set.Done)))
                .OrderByDescending(item => item.FinishedAt)
                .FirstOrDefault();
            var result = session?.Exercises.Single(item =>
                item.Row == context.ExerciseRow && item.Name == context.ExerciseName);
            var sheetWeek = exercise?.Weeks.SingleOrDefault(item => item.Number == week);
            var hasSheetResult = !string.IsNullOrWhiteSpace(sheetWeek?.ExistingLoad);

            if (session is not null && result is not null)
            {
                // Неотправленная запись устройства важнее старого снимка таблицы.
                // Если после синхронизации ячейки исправили вручную, используем их новое содержимое.
                if (session.SyncedAt is null || !hasSheetResult || MatchesSheet(result, sheetWeek!))
                {
                    return FromLocal(session, result);
                }
            }
            if (hasSheetResult)
            {
                return FromSheet(sheetWeek!);
            }
        }
        return null;
    }

    private static bool MatchesSheet(ExerciseResult result, WeekPrescription week)
    {
        var completed = result.Sets.Where(set => set.Done).ToList();
        var load = string.Join("\n", completed.Select(set =>
            $"{set.Weight?.ToString("0.##", CultureInfo.InvariantCulture)} кг × {set.Repetitions}"));
        var reserve = WorkoutRules.FormatReserves(completed);
        return load == week.ExistingLoad.Replace("\r\n", "\n")
            && (reserve.Length == 0 || reserve == week.ExistingReserve);
    }

    private static PreviousExercise FromLocal(WorkoutSession session, ExerciseResult exercise)
    {
        var sets = exercise.Sets.Select((set, index) => (Set: set, Number: index + 1))
            .Where(item => item.Set.Done).ToList();
        var valid = sets.All(item => WorkoutRules.IsValid(item.Set));
        return new PreviousExercise
        {
            Week = session.Week,
            FinishedAt = session.FinishedAt,
            Source = session.SyncedAt is null ? "На устройстве · ещё не отправлено" : "История устройства",
            PlannedSets = exercise.Sets.Count,
            Repetitions = exercise.PlannedRepetitions,
            TechniqueMaintained = exercise.TechniqueMaintained,
            Parsed = valid,
            Complete = valid && sets.Count == exercise.Sets.Count,
            Sets = sets.Where(item => WorkoutRules.IsValid(item.Set)).Select(item =>
                new PreviousSet(item.Number, item.Set.Weight!.Value,
                    item.Set.Repetitions!.Value, item.Set.Reserve)).ToList()
        };
    }

    public static PreviousExercise FromSheet(WeekPrescription week)
    {
        var loads = SplitValues(week.ExistingLoad);
        var reserves = SplitValues(week.ExistingReserve);
        var sets = new List<PreviousSet>();
        var parsed = loads.Length is > 0 and <= 30;
        foreach (var (load, index) in loads.Select((value, index) => (value, index)))
        {
            var match = LoadPattern().Match(load);
            if (!match.Success
                || !decimal.TryParse(match.Groups[1].Value.Replace(',', '.'),
                    NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var weight)
                || !int.TryParse(match.Groups[2].Value, out var repetitions)
                || weight is < 0 or > 2000 || repetitions is < 1 or > 500)
            {
                parsed = false;
                break;
            }
            int? reserve = null;
            // Одиночное число не размножаем по всем подходам: это было бы догадкой.
            if (reserves.Length == loads.Length
                && int.TryParse(reserves[index], out var number) && number is >= 0 and <= 20)
            {
                reserve = number;
            }
            sets.Add(new PreviousSet(index + 1, weight, repetitions, reserve));
        }
        return new PreviousExercise
        {
            Week = week.Number,
            Source = "Сохранённая Google-таблица",
            PlannedSets = week.Sets,
            Repetitions = week.Repetitions,
            RawLoad = week.ExistingLoad,
            RawReserve = week.ExistingReserve,
            Parsed = parsed,
            Complete = parsed && loads.Length == week.Sets,
            Sets = parsed ? sets : []
        };
    }

    private static string[] SplitValues(string value)
    {
        return value.Split(['\r', '\n', ';', '/'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    // Понимаем собственный формат экспорта и обычные «60x10», «60,5 кг × 10».
    // Свободный текст вроде «тяжело, 60» сохраняем для показа, но не интерпретируем.
    [GeneratedRegex(@"^\s*(\d+(?:[.,]\d+)?)\s*(?:кг|kg)?\s*[×xх*]\s*(\d+)\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex LoadPattern();
}
