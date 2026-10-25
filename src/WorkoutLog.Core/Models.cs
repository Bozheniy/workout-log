namespace WorkoutLog.Core;

// Эти классы описывают данные. Они не зависят от браузера, Blazor и Google.
public sealed class AppSettings
{
    public string SpreadsheetUrl { get; set; } = "";
    public string GoogleClientId { get; set; } = "";
    public int RestSeconds { get; set; } = 120;
    public bool TimerEnabled { get; set; } = true;
}

public sealed class LocalState
{
    public int SchemaVersion { get; set; } = 1;
    public int Revision { get; set; }
    public AppSettings Settings { get; set; } = new();
    public WorkoutPlan? Plan { get; set; }
    public int SelectedWeek { get; set; } = 1;
    public int SelectedDay { get; set; }
    public List<WorkoutSession> Sessions { get; set; } = [];
    public List<PendingUpload> Outbox { get; set; } = [];
    // Настройки привязаны к конкретному упражнению конкретного листа.
    public Dictionary<string, ExerciseProgressionSettings> ProgressionSettings { get; set; } = [];
}

public sealed class WorkoutPlan
{
    public string SpreadsheetId { get; set; } = "";
    public int SheetId { get; set; }
    public DateTimeOffset DownloadedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<string> CoachNotes { get; set; } = [];
    public List<TrainingDay> Days { get; set; } = [];
    public int WeekCount => Days.SelectMany(day => day.Exercises)
        .SelectMany(exercise => exercise.Weeks).Select(week => week.Number).DefaultIfEmpty(1).Max();
}

public sealed class TrainingDay
{
    public int HeaderRow { get; set; }
    public string Name { get; set; } = "";
    public List<PlannedExercise> Exercises { get; set; } = [];
}

public sealed class PlannedExercise
{
    public int Row { get; set; }
    public string Name { get; set; } = "";
    public List<WeekPrescription> Weeks { get; set; } = [];
}

public sealed class WeekPrescription
{
    public int Number { get; set; }
    public int Sets { get; set; }
    public string Repetitions { get; set; } = "";
    // Номер столбца Google начинается с 1: A = 1, B = 2.
    public int LoadColumn { get; set; }
    public string ExistingLoad { get; set; } = "";
    public string ExistingReserve { get; set; } = "";
}

public sealed class WorkoutSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SpreadsheetId { get; set; } = "";
    public int SheetId { get; set; }
    public int DayRow { get; set; }
    public string DayName { get; set; } = "";
    public int Week { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? SyncedAt { get; set; }
    // Старые незавершённые записи открываются как сохранённые черновики.
    public bool IsPaused { get; set; } = true;
    public List<ExerciseResult> Exercises { get; set; } = [];
    public int CompletedSets => Exercises.Sum(exercise => exercise.CompletedSets);
    public int TotalSets => Exercises.Sum(exercise => exercise.TotalSets);
    public decimal Volume => Exercises.SelectMany(exercise => exercise.Entries).Sum(exercise => exercise.Sets
        .Where(set => set.Done).Sum(set => (set.Weight ?? 0) * (set.Repetitions ?? 0)));
}

// Обычное упражнение и часть суперсета используют одинаковые поля ввода.
public class ExercisePerformance
{
    public string Name { get; set; } = "";
    public string PlannedRepetitions { get; set; } = "";
    public bool Skipped { get; set; }
    public string QuickWeightInput { get; set; } = "";
    public string QuickRepetitionsInput { get; set; } = "";
    // null означает, что пользователь ещё не оценивал технику.
    public bool? TechniqueMaintained { get; set; }
    public List<SetResult> Sets { get; set; } = [];
}

public sealed class ExerciseResult : ExercisePerformance
{
    public int Row { get; set; }
    public int LoadColumn { get; set; }
    public string BaselineLoad { get; set; } = "";
    public string BaselineReserve { get; set; } = "";
    // Одна строка таблицы содержит несколько независимых результатов.
    // Пустой список сохраняет совместимость с прежними записями.
    public List<ExercisePerformance> Parts { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<ExercisePerformance> Entries => Parts.Count > 0 ? Parts : [this];
    public int CompletedSets => Entries.Sum(entry => entry.Sets.Count(set => set.Done));
    public int TotalSets => Entries.Sum(entry => entry.Sets.Count);
    public bool IsComplete => Entries.All(entry => entry.Skipped || entry.Sets.All(set => set.Done));
    public bool IsSkipped => Entries.All(entry => entry.Skipped);

    public ExercisePerformance Entry(int? partIndex)
    {
        if (partIndex is null && Parts.Count == 0)
        {
            return this;
        }
        if (partIndex is >= 0 && partIndex < Parts.Count)
        {
            return Parts[partIndex.Value];
        }
        throw new InvalidOperationException("Выберите упражнение внутри суперсета.");
    }
}

public sealed class SetResult
{
    // Сохраняем ввод как текст, чтобы промежуточное «60,» не превращалось в «60».
    public string WeightInput { get; set; } = "";
    public decimal? Weight { get; set; }
    public int? Repetitions { get; set; }
    public int? Reserve { get; set; }
    public string ReserveInput { get; set; } = "";
    public bool Done { get; set; }
}

public enum UploadStatus
{
    Pending,
    NeedsSignIn,
    Conflict,
    PermissionDenied
}

public sealed class PendingUpload
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; set; } = "";
    public string SpreadsheetId { get; set; } = "";
    public int SheetId { get; set; }
    public int Week { get; set; }
    public int DayRow { get; set; }
    public string DayName { get; set; } = "";
    public UploadStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset RetryAfter { get; set; }
    public string Message { get; set; } = "";
    public List<CellUpdate> Cells { get; set; } = [];
}

public sealed class CellUpdate
{
    public int Row { get; set; }
    public int LoadColumn { get; set; }
    public string ExerciseName { get; set; } = "";
    public int PlannedSets { get; set; }
    public string PlannedRepetitions { get; set; } = "";
    public string ExpectedLoad { get; set; } = "";
    public string ExpectedReserve { get; set; } = "";
    public string Load { get; set; } = "";
    public string Reserve { get; set; } = "";
    // Пустой запас означает: ячейку Google вообще не трогаем.
    public bool WritesReserve => !string.IsNullOrWhiteSpace(Reserve);
}
