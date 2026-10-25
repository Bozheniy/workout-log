using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkoutLog.Core;

public static partial class SupersetRules
{
    public static List<string> Names(string name)
    {
        // Знак «+» сам по себе может означать дропсет. Нужна явная пометка тренера.
        if (!Marker().IsMatch(name))
        {
            return [];
        }
        var names = Marker().Replace(name, "").Split('+', StringSplitOptions.TrimEntries)
            .Select(part => Regex.Replace(part, @"\s+", " ").Trim(' ', ':', '-', '(', ')')).ToList();
        return names.Count is >= 2 and <= 6 && names.All(part => part.Length > 0)
            && names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Count ? names : [];
    }

    public static string Repetitions(string value, int index, int count)
    {
        var ranges = value.Split('+', StringSplitOptions.TrimEntries);
        return ranges.Length == count ? ranges[index] : value;
    }

    public static bool HasInput(ExercisePerformance entry)
    {
        return entry.Skipped || entry.QuickWeightInput.Length > 0 || entry.QuickRepetitionsInput.Length > 0
            || entry.TechniqueMaintained is not null || entry.Sets.Any(set => set.Done
                || set.Weight.HasValue || set.Repetitions.HasValue || set.Reserve.HasValue
                || set.WeightInput.Length > 0 || set.ReserveInput.Length > 0);
    }

    public static void PrepareEmptyEntries(WorkoutSession session)
    {
        if (session.FinishedAt is not null)
        {
            return;
        }
        foreach (var exercise in session.Exercises.Where(item => item.Parts.Count == 0 && !HasInput(item)))
        {
            Split(exercise);
        }
    }

    public static void Enable(WorkoutSession session, int row)
    {
        if (session.FinishedAt is not null || session.IsPaused)
        {
            throw new InvalidOperationException("Сначала продолжите тренировку.");
        }
        Split(session.Exercises.Single(exercise => exercise.Row == row));
    }

    private static void Split(ExerciseResult exercise)
    {
        var names = Names(exercise.Name);
        if (exercise.Parts.Count > 0 || names.Count == 0)
        {
            return;
        }
        for (var index = 0; index < names.Count; index++)
        {
            // При ручном разделении старого черновика введённое остаётся у первой части.
            // Другим упражнениям вес и повторы не приписываем.
            var entry = index == 0
                ? JsonSerializer.Deserialize<ExercisePerformance>(JsonSerializer.Serialize<ExercisePerformance>(exercise))!
                : new ExercisePerformance
                {
                    Sets = exercise.Sets.Select(_ => new SetResult()).ToList()
                };
            entry.Name = names[index];
            entry.PlannedRepetitions = Repetitions(exercise.PlannedRepetitions, index, names.Count);
            exercise.Parts.Add(entry);
        }
    }

    [GeneratedRegex(@"\b(?:супер\s*сет|superset)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Marker();
}
