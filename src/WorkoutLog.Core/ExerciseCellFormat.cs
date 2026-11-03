using System.Globalization;

namespace WorkoutLog.Core;

// Формат ячейки читается человеком и позволяет восстановить каждую часть суперсета.
public static class ExerciseCellFormat
{
    public static string Load(ExerciseResult exercise)
    {
        return exercise.Parts.Count == 0
            ? string.Join("\n", exercise.Sets.Where(set => set.Done).Select(SetLoad))
            : Rounds(exercise, set => set.Done
                ? $"{set.Weight?.ToString("0.##", CultureInfo.InvariantCulture)} кг×{set.Repetitions}"
                : "—");
    }

    public static string Reserve(ExerciseResult exercise)
    {
        if (!exercise.Entries.SelectMany(entry => entry.Sets).Any(set => set.Done && set.Reserve.HasValue))
        {
            return "";
        }
        return exercise.Parts.Count == 0
            ? WorkoutRules.FormatReserves(exercise.Sets.Where(set => set.Done))
            : Rounds(exercise, set => set.Done
                ? set.Reserve?.ToString(CultureInfo.InvariantCulture) ?? "—" : "—");
    }

    public static string SetLoad(SetResult set)
    {
        return $"{set.Weight?.ToString("0.##", CultureInfo.InvariantCulture)} кг × {set.Repetitions}";
    }

    private static string Rounds(ExerciseResult exercise, Func<SetResult, string> value)
    {
        // Одна строка — один круг. Порядок частей совпадает с названием в плане.
        // Прочерк оставляет место пропущенной части и не сдвигает соседние результаты.
        var count = exercise.Parts.Max(entry => entry.Sets.Count);
        var rounds = new List<string>();
        for (var index = 0; index < count; index++)
        {
            var parts = exercise.Parts.Select(entry =>
                index < entry.Sets.Count ? value(entry.Sets[index]) : "—");
            rounds.Add(string.Join("/", parts));
        }
        return string.Join("\n", rounds);
    }

    public static string? ReadPart(string cell, string name, int partIndex = -1, int partCount = 0)
    {
        // Прежние записи с названиями по-прежнему читаются после обновления приложения.
        var legacy = ReadNamedPart(cell, name);
        if (legacy is not null)
        {
            return legacy;
        }
        if (partCount is < 2 or > 6 || partIndex < 0 || partIndex >= partCount)
        {
            return null;
        }

        var rounds = cell.Split(['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (rounds.Length is < 1 or > 30)
        {
            return null;
        }
        var values = new List<string>();
        foreach (var round in rounds)
        {
            var parts = round.Split('/', StringSplitOptions.TrimEntries);
            // Без точного числа частей нельзя надёжно определить, к кому относится вес.
            if (parts.Length != partCount || parts.Any(string.IsNullOrWhiteSpace))
            {
                return null;
            }
            values.Add(parts[partIndex]);
        }
        return string.Join("\n", values);
    }

    private static string? ReadNamedPart(string cell, string name)
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
