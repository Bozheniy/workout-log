using System.Globalization;

namespace WorkoutLog.Core;

// Формат ячейки читается человеком и позволяет восстановить каждую часть суперсета.
public static class ExerciseCellFormat
{
    public static string Load(ExerciseResult exercise)
    {
        return exercise.Parts.Count == 0
            ? string.Join("\n", exercise.Sets.Where(set => set.Done).Select(SetLoad))
            : Sections(exercise, set => set.Done ? SetLoad(set) : "—");
    }

    public static string Reserve(ExerciseResult exercise)
    {
        if (!exercise.Entries.SelectMany(entry => entry.Sets).Any(set => set.Done && set.Reserve.HasValue))
        {
            return "";
        }
        return exercise.Parts.Count == 0
            ? WorkoutRules.FormatReserves(exercise.Sets.Where(set => set.Done))
            : Sections(exercise, set => set.Done
                ? set.Reserve?.ToString(CultureInfo.InvariantCulture) ?? "—" : "—");
    }

    public static string SetLoad(SetResult set)
    {
        return $"{set.Weight?.ToString("0.##", CultureInfo.InvariantCulture)} кг × {set.Repetitions}";
    }

    private static string Sections(ExerciseResult exercise, Func<SetResult, string> value)
    {
        return string.Join("\n\n", exercise.Parts.Select((entry, index) =>
            $"{index + 1}. {entry.Name}\n" + string.Join("\n", entry.Sets.Select((set, number) =>
                $"{number + 1}) {value(set)}"))));
    }

    public static string? ReadPart(string cell, string name)
    {
        var lines = cell.Replace("\r\n", "\n").Split('\n');
        var header = Array.FindIndex(lines, line =>
        {
            var separator = line.IndexOf(". ", StringComparison.Ordinal);
            return separator > 0 && int.TryParse(line[..separator], out _)
                && line[(separator + 2)..] == name;
        });
        if (header < 0)
        {
            return null;
        }
        var values = new List<string>();
        foreach (var line in lines.Skip(header + 1))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                break;
            }
            var separator = line.IndexOf(") ", StringComparison.Ordinal);
            if (separator < 1 || !int.TryParse(line[..separator], out var number)
                || number != values.Count + 1)
            {
                return null;
            }
            values.Add(line[(separator + 2)..]);
        }
        return values.Count > 0 ? string.Join("\n", values) : null;
    }
}
