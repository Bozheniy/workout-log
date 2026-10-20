using System.Text;
using System.Text.RegularExpressions;

namespace WorkoutLog.Core;

public static partial class SheetParser
{
    public static (string SpreadsheetId, int SheetId) ParseUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || uri.Host != "docs.google.com")
        {
            throw new InvalidOperationException("Вставьте ссылку на таблицу с адреса https://docs.google.com.");
        }

        var idMatch = SpreadsheetIdPattern().Match(uri.AbsolutePath);
        var gidMatch = SheetIdPattern().Match(uri.Query + "&" + uri.Fragment.TrimStart('#'));
        if (!idMatch.Success || !gidMatch.Success || !int.TryParse(gidMatch.Groups[1].Value, out var sheetId))
        {
            throw new InvalidOperationException("В ссылке должны быть ID таблицы и gid нужного листа.");
        }

        return (idMatch.Groups[1].Value, sheetId);
    }

    public static WorkoutPlan Parse(string csv, string spreadsheetId, int sheetId)
    {
        return ParseRows(ReadCsv(csv), spreadsheetId, sheetId);
    }

    public static WorkoutPlan ParseRows(List<List<string>> rows, string spreadsheetId, int sheetId)
    {
        var plan = new WorkoutPlan { SpreadsheetId = spreadsheetId, SheetId = sheetId };
        TrainingDay? currentDay = null;
        List<(int Number, int Column)> weekColumns = [];
        var verifiedColumns = false;

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var firstCell = Cell(row, 0).Trim();
            var foundWeeks = row.Select((value, index) => (Match: WeekPattern().Match(value), Column: index))
                .Where(item => item.Match.Success)
                .Select(item => (Number: int.Parse(item.Match.Groups[1].Value), item.Column))
                .ToList();

            if (foundWeeks.Count > 0 && firstCell.Length > 0)
            {
                currentDay = new TrainingDay { HeaderRow = rowIndex + 1, Name = firstCell };
                plan.Days.Add(currentDay);
                weekColumns = foundWeeks;
                verifiedColumns = false;
                continue;
            }

            if (currentDay is null)
            {
                if (firstCell.Length > 0)
                {
                    plan.CoachNotes.Add(firstCell);
                }
                continue;
            }

            if (firstCell.Length == 0)
            {
                if (weekColumns.Count > 0 && Cell(row, weekColumns[0].Column).Trim().ToLowerInvariant() == "подходы")
                {
                    verifiedColumns = weekColumns.All(week =>
                        Cell(row, week.Column).Trim().ToLowerInvariant() == "подходы"
                        && Cell(row, week.Column + 1).Trim().ToLowerInvariant() == "повторы"
                        && Cell(row, week.Column + 2).Trim().ToLowerInvariant() == "нагрузка"
                        && Cell(row, week.Column + 3).Trim().ToLowerInvariant() == "запас");
                }
                continue;
            }

            var exercise = new PlannedExercise { Row = rowIndex + 1, Name = firstCell };
            foreach (var (number, column) in weekColumns)
            {
                if (!int.TryParse(Cell(row, column), out var sets) || sets is < 1 or > 30)
                {
                    continue;
                }

                exercise.Weeks.Add(new WeekPrescription
                {
                    Number = number,
                    Sets = sets,
                    Repetitions = Cell(row, column + 1),
                    LoadColumn = column + 3,
                    ExistingLoad = Cell(row, column + 2),
                    ExistingReserve = Cell(row, column + 3)
                });
            }

            if (exercise.Weeks.Count > 0)
            {
                if (!verifiedColumns)
                {
                    throw new InvalidOperationException(
                        "Порядок столбцов изменился. Ожидаются «подходы, повторы, нагрузка, запас». Запись остановлена.");
                }
                currentDay.Exercises.Add(exercise);
            }
        }

        if (plan.Days.Count == 0 || plan.Days.Any(day => day.Exercises.Count == 0))
        {
            throw new InvalidOperationException(
                "Не удалось распознать план. Ожидаются блоки «неделя 1…» и столбцы «подходы / повторы / нагрузка / запас».");
        }

        return plan;
    }

    // CSV может содержать запятые, кавычки и переносы строк внутри одной ячейки.
    // Обычный Split(',') для Google-таблиц непригоден.
    public static List<List<string>> ReadCsv(string csv)
    {
        var result = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < csv.Length; index++)
        {
            var character = csv[index];
            if (character == '"')
            {
                if (inQuotes && index + 1 < csv.Length && csv[index + 1] == '"')
                {
                    cell.Append('"');
                    index++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (character == ',' && !inQuotes)
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if ((character == '\n' || character == '\r') && !inQuotes)
            {
                if (character == '\r' && index + 1 < csv.Length && csv[index + 1] == '\n')
                {
                    index++;
                }
                row.Add(cell.ToString());
                result.Add(row);
                row = [];
                cell.Clear();
            }
            else
            {
                cell.Append(character);
            }
        }

        if (inQuotes)
        {
            throw new InvalidOperationException("Google вернул неполный CSV. Попробуйте загрузить план ещё раз.");
        }
        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            result.Add(row);
        }
        return result;
    }

    public static string ColumnName(int column)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(column, 1);
        var name = "";
        while (column > 0)
        {
            column--;
            name = (char)('A' + column % 26) + name;
            column /= 26;
        }
        return name;
    }

    public static string Cell(List<string> row, int index)
    {
        return index >= 0 && index < row.Count ? row[index] : "";
    }

    [GeneratedRegex(@"/spreadsheets/d/([a-zA-Z0-9_-]+)")]
    private static partial Regex SpreadsheetIdPattern();

    [GeneratedRegex(@"(?:[?&#]|^)gid=(\d+)")]
    private static partial Regex SheetIdPattern();

    [GeneratedRegex(@"^неделя\s+(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex WeekPattern();
}
