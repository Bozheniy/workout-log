using System.Globalization;

namespace WorkoutLog.Core;

public static class WorkoutRules
{
    public static WorkoutSession Start(WorkoutPlan plan, TrainingDay day, int week)
    {
        var session = new WorkoutSession
        {
            SpreadsheetId = plan.SpreadsheetId,
            SheetId = plan.SheetId,
            DayRow = day.HeaderRow,
            DayName = day.Name,
            Week = week
        };

        foreach (var exercise in day.Exercises)
        {
            var prescription = exercise.Weeks.SingleOrDefault(item => item.Number == week);
            if (prescription is null)
            {
                continue;
            }
            session.Exercises.Add(new ExerciseResult
            {
                Row = exercise.Row,
                Name = exercise.Name,
                LoadColumn = prescription.LoadColumn,
                PlannedRepetitions = prescription.Repetitions,
                BaselineLoad = prescription.ExistingLoad,
                BaselineReserve = prescription.ExistingReserve,
                Sets = Enumerable.Range(0, prescription.Sets).Select(_ => new SetResult()).ToList()
            });
        }
        return session;
    }

    public static bool IsValid(SetResult set)
    {
        return set.Weight is >= 0 and <= 2000
            && set.Repetitions is >= 1 and <= 500
            && set.Reserve is >= 0 and <= 20;
    }

    public static void FillRemainingSets(WorkoutSession session, int exerciseRow, string weightInput, string repetitionsInput)
    {
        if (session.FinishedAt is not null)
        {
            throw new InvalidOperationException("Завершённая тренировка доступна для просмотра.");
        }
        var exercise = session.Exercises.Single(item => item.Row == exerciseRow);
        if (exercise.Skipped)
        {
            throw new InvalidOperationException("Сначала верните пропущенное упражнение.");
        }

        var normalizedWeight = weightInput.Trim().Replace(',', '.');
        if (!decimal.TryParse(normalizedWeight, NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var weight) || weight is < 0 or > 2000
            || !int.TryParse(repetitionsInput.Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out var repetitions) || repetitions is < 1 or > 500)
        {
            throw new InvalidOperationException("Введите вес от 0 до 2000 кг и повторы от 1 до 500.");
        }
        var remaining = exercise.Sets.Where(set => !set.Done).ToList();
        if (remaining.Count == 0)
        {
            throw new InvalidOperationException("Все подходы уже выполнены.");
        }
        // Проверяем всё до изменения. Запас и отметки выполнения остаются фактическими.
        foreach (var set in remaining)
        {
            set.Weight = weight;
            set.WeightInput = weightInput.Trim();
            set.Repetitions = repetitions;
        }
    }

    public static PendingUpload Complete(WorkoutSession session)
    {
        if (session.FinishedAt is not null)
        {
            throw new InvalidOperationException("Тренировка уже завершена.");
        }
        if (session.CompletedSets == 0)
        {
            throw new InvalidOperationException("Сначала запишите хотя бы один подход.");
        }
        if (session.Exercises.Any(exercise => !exercise.Skipped && exercise.Sets.Any(set => !set.Done)))
        {
            throw new InvalidOperationException("Отметьте оставшиеся подходы или пропустите упражнение.");
        }
        if (session.Exercises.SelectMany(exercise => exercise.Sets).Any(set => set.Done && !IsValid(set)))
        {
            throw new InvalidOperationException("Проверьте вес, повторы и запас выполненных подходов.");
        }

        var upload = new PendingUpload
        {
            SessionId = session.Id,
            SpreadsheetId = session.SpreadsheetId,
            SheetId = session.SheetId,
            Week = session.Week,
            DayRow = session.DayRow,
            DayName = session.DayName
        };

        foreach (var exercise in session.Exercises)
        {
            var completed = exercise.Sets.Where(set => set.Done).ToList();
            // Полностью пропущенное упражнение оставляет таблицу без изменений.
            if (completed.Count == 0)
            {
                continue;
            }

            upload.Cells.Add(new CellUpdate
            {
                Row = exercise.Row,
                LoadColumn = exercise.LoadColumn,
                ExerciseName = exercise.Name,
                PlannedSets = exercise.Sets.Count,
                PlannedRepetitions = exercise.PlannedRepetitions,
                ExpectedLoad = exercise.BaselineLoad,
                ExpectedReserve = exercise.BaselineReserve,
                Load = string.Join("\n", completed.Select(set =>
                    $"{set.Weight!.Value.ToString("0.##", CultureInfo.InvariantCulture)} кг × {set.Repetitions}")),
                Reserve = string.Join(" / ", completed.Select(set => set.Reserve))
            });
        }
        session.FinishedAt = DateTimeOffset.UtcNow;
        return upload;
    }
}

public enum ReconciliationResult
{
    SafeToWrite,
    AlreadyApplied,
    Conflict
}

public static class UploadReconciliation
{
    public static ReconciliationResult Evaluate(PendingUpload upload, WorkoutPlan remote)
    {
        if (upload.SpreadsheetId != remote.SpreadsheetId || upload.SheetId != remote.SheetId)
        {
            return ReconciliationResult.Conflict;
        }
        var day = remote.Days.SingleOrDefault(item =>
            item.HeaderRow == upload.DayRow && item.Name == upload.DayName);
        if (day is null)
        {
            return ReconciliationResult.Conflict;
        }

        var allApplied = true;
        foreach (var cell in upload.Cells)
        {
            var exercise = day.Exercises.SingleOrDefault(item =>
                item.Row == cell.Row && item.Name == cell.ExerciseName);
            var week = exercise?.Weeks.SingleOrDefault(item => item.Number == upload.Week);
            if (week is null || week.LoadColumn != cell.LoadColumn
                || week.Sets != cell.PlannedSets || week.Repetitions != cell.PlannedRepetitions)
            {
                return ReconciliationResult.Conflict;
            }

            var applied = week.ExistingLoad == cell.Load && week.ExistingReserve == cell.Reserve;
            var unchanged = week.ExistingLoad == cell.ExpectedLoad && week.ExistingReserve == cell.ExpectedReserve;
            if (week.ExistingLoad.StartsWith('=') || week.ExistingReserve.StartsWith('='))
            {
                return ReconciliationResult.Conflict;
            }
            if (!applied && !unchanged)
            {
                return ReconciliationResult.Conflict;
            }
            allApplied &= applied;
        }

        // Если ответ на запись потерялся, повторное чтение узнает уже записанный результат.
        // Мы обновляем фиксированные ячейки, а не добавляем дублирующиеся строки.
        return allApplied ? ReconciliationResult.AlreadyApplied : ReconciliationResult.SafeToWrite;
    }
}
