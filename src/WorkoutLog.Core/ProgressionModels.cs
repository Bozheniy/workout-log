namespace WorkoutLog.Core;

public sealed class ExerciseProgressionSettings
{
    public decimal WeightStep { get; set; } = 2.5m;
    public int FirstSetReserve { get; set; } = 2;
    public int LastSetReserve { get; set; }

    public int ReserveFor(int index, int count)
    {
        if (count <= 1)
        {
            return LastSetReserve;
        }
        // Между первым и последним подходами плавно уменьшаем заданный запас.
        var fraction = (decimal)index / (count - 1);
        return (int)Math.Round(FirstSetReserve + (LastSetReserve - FirstSetReserve) * fraction,
            MidpointRounding.AwayFromZero);
    }
}

public sealed record ProgressionContext(
    string SpreadsheetId,
    int SheetId,
    int DayRow,
    int ExerciseRow,
    string ExerciseName,
    int Week,
    int SetCount,
    string Repetitions,
    DateTimeOffset? StartedAt = null)
{
    public string SettingsKey => $"{SpreadsheetId}:{SheetId}:{DayRow}:{ExerciseRow}:{ExerciseName}";
}

public sealed class PreviousExercise
{
    public int Week { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string Source { get; init; } = "";
    public string Repetitions { get; init; } = "";
    public int PlannedSets { get; init; }
    public List<PreviousSet> Sets { get; init; } = [];
    public string RawLoad { get; init; } = "";
    public string RawReserve { get; init; } = "";
    public bool Parsed { get; init; }
    public bool Complete { get; init; }
    public bool? TechniqueMaintained { get; init; }
}

public sealed record PreviousSet(int Number, decimal Weight, int Repetitions, int? Reserve);

public enum ProgressionAction
{
    FirstWorkout,
    NeedData,
    PlanChanged,
    AddRepetitions,
    AddWeight,
    Adjust,
    Hold
}

public sealed record SetGoal(int Number, decimal? Weight, int Repetitions, int Reserve);

public sealed class ProgressionAdvice
{
    public ProgressionAction Action { get; init; }
    public string Title { get; init; } = "";
    public string Explanation { get; init; } = "";
    public bool TechniqueUnknown { get; init; }
    public List<SetGoal> Goals { get; init; } = [];
}
