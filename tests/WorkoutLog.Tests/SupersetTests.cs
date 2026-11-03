using System.Text.Json;
using WorkoutLog.Core;

namespace WorkoutLog.Tests;

public sealed class SupersetTests
{
    public const string Name = "Тяга блока + Отведения гантелей СУПЕРСЕТ";

    public static WorkoutPlan Plan()
    {
        var plan = PlanAndQueueTests.Plan();
        var exercise = plan.Days[0].Exercises[0];
        exercise.Name = Name;
        foreach (var week in exercise.Weeks)
        {
            week.Sets = 2;
            week.Repetitions = "8-12";
            week.ExistingLoad = "";
            week.ExistingReserve = "";
        }
        return plan;
    }

    public static WorkoutSession Start(WorkoutPlan? plan = null)
    {
        plan ??= Plan();
        return WorkoutRules.Start(plan, plan.Days[0], 1);
    }

    [Fact]
    public void Parts_have_independent_sets_and_bulk_entry_does_not_complete_the_partner()
    {
        var session = Start();
        var exercise = session.Exercises[0];
        Assert.Equal(["Тяга блока", "Отведения гантелей"], exercise.Parts.Select(part => part.Name));
        WorkoutRules.FillRemainingSets(session, 5, "40,5", "10", 0);
        Assert.False(exercise.IsComplete);
        Assert.Equal(2, session.CompletedSets);
        Assert.Equal(4, session.TotalSets);
        Assert.Equal(810, session.Volume);
        Assert.All(exercise.Parts[1].Sets, set => Assert.Null(set.Weight));
        Assert.Throws<InvalidOperationException>(() => WorkoutRules.Complete(session));

        WorkoutRules.FillRemainingSets(session, 5, "6", "15", 1);
        Assert.True(exercise.IsComplete);
        Assert.Equal(990, session.Volume);
        Assert.Equal(4, session.CompletedSets);
    }

    [Fact]
    public void Export_keeps_two_exercises_in_one_cell_and_omits_unentered_reserve()
    {
        var session = Start();
        WorkoutRules.FillRemainingSets(session, 5, "40,5", "10", 0);
        WorkoutRules.FillRemainingSets(session, 5, "6", "15", 1);
        var cell = Assert.Single(WorkoutRules.Complete(session).Cells);
        Assert.Equal(5, cell.Row);
        Assert.Equal(4, cell.LoadColumn);
        Assert.Equal(Name, cell.ExerciseName);
        Assert.Equal(2, cell.PlannedSets);
        Assert.Equal("40.5 кг×10/6 кг×15\n40.5 кг×10/6 кг×15", cell.Load);
        Assert.False(cell.WritesReserve);
    }

    [Fact]
    public void Partial_results_preserve_round_numbers_and_each_parts_optional_reserve()
    {
        var session = Start();
        var exercise = session.Exercises[0];
        WorkoutRules.FillRemainingSets(session, 5, "40", "10", 0);
        WorkoutRules.FillRemainingSets(session, 5, "6", "15", 1);
        exercise.Parts[0].Sets[1].Reserve = 2;
        exercise.Parts[1].Sets[0].Done = false;
        exercise.Parts[1].Skipped = true;
        var cell = Assert.Single(WorkoutRules.Complete(session).Cells);
        Assert.Equal("40 кг×10/—\n40 кг×10/6 кг×15", cell.Load);
        Assert.Equal("—/—\n2/—", cell.Reserve);
        var week = new WeekPrescription { Number = 1, Sets = 2, Repetitions = "8-12", ExistingLoad = cell.Load, ExistingReserve = cell.Reserve };
        var first = PreviousWorkoutFinder.FromSheet(week, "Тяга блока", partIndex: 0, partCount: 2);
        var second = PreviousWorkoutFinder.FromSheet(week, "Отведения гантелей", partIndex: 1, partCount: 2);
        Assert.True(first.Complete);
        Assert.Null(first.Sets[0].Reserve);
        Assert.Equal(2, first.Sets[1].Reserve);
        Assert.True(second.Parsed);
        Assert.False(second.Complete);
        Assert.Equal(2, Assert.Single(second.Sets).Number);
        Assert.Null(second.Sets[0].Reserve);
    }

    [Fact]
    public void Compact_export_groups_different_values_by_round_including_three_parts()
    {
        var plan = Plan();
        plan.Days[0].Exercises[0].Name = "Тяга + Жим + Отведения СУПЕРСЕТ";
        var session = Start(plan);
        WorkoutRules.FillRemainingSets(session, 5, "10", "10", 0);
        WorkoutRules.FillRemainingSets(session, 5, "10", "10", 1);
        WorkoutRules.FillRemainingSets(session, 5, "0", "20", 2);
        var parts = session.Exercises[0].Parts;
        parts[0].Sets[1].Weight = 12.5m;
        parts[0].Sets[1].Repetitions = 8;
        parts[1].Sets[1].Repetitions = 9;
        parts[2].Sets[1].Repetitions = 18;
        var cell = Assert.Single(WorkoutRules.Complete(session).Cells);
        Assert.Equal("10 кг×10/10 кг×10/0 кг×20\n12.5 кг×8/10 кг×9/0 кг×18", cell.Load);

        var week = new WeekPrescription { Sets = 2, ExistingLoad = cell.Load };
        for (var index = 0; index < parts.Count; index++)
        {
            var previous = PreviousWorkoutFinder.FromSheet(week, parts[index].Name, partIndex: index, partCount: 3);
            Assert.True(previous.Complete);
            Assert.Equal(parts[index].Sets.Select(set => set.Weight), previous.Sets.Select(set => (decimal?)set.Weight));
            Assert.Equal(parts[index].Sets.Select(set => set.Repetitions), previous.Sets.Select(set => (int?)set.Repetitions));
        }
    }

    [Theory]
    [InlineData("10 кг×10/10 кг×10\n12,5 кг×8/8 кг×12", "2/—\n1/3")]
    [InlineData("1. Тяга блока\n1) 10 кг × 10\n2) 12.5 кг × 8\n\n2. Отведения гантелей\n1) 10 кг × 10\n2) 8 кг × 12",
        "1. Тяга блока\n1) 2\n2) 1\n\n2. Отведения гантелей\n1) —\n2) 3")]
    public void Sheet_history_reads_both_formats_with_independent_weights_reps_and_reserves(string load, string reserve)
    {
        var plan = Plan();
        var week = plan.Days[0].Exercises[0].Weeks[0];
        week.ExistingLoad = load;
        week.ExistingReserve = reserve;
        var state = new LocalState { Plan = plan };
        var context = new ProgressionContext("test-sheet", 42, 2, 5, Name, 2, 2, "8-12", PartName: "Тяга блока");
        var first = PreviousWorkoutFinder.Find(state, context)!;
        var second = PreviousWorkoutFinder.Find(state, context with { PartName = "Отведения гантелей" })!;
        Assert.True(first.Complete);
        Assert.True(second.Complete);
        Assert.Equal([new PreviousSet(1, 10, 10, 2), new PreviousSet(2, 12.5m, 8, 1)], first.Sets);
        Assert.Equal([new PreviousSet(1, 10, 10, null), new PreviousSet(2, 8, 12, 3)], second.Sets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Synced_legacy_history_keeps_local_technique_but_respects_remote_edits(bool edited)
    {
        var plan = Plan();
        var session = Start(plan);
        WorkoutRules.FillRemainingSets(session, 5, "40", "10", 0);
        WorkoutRules.FillRemainingSets(session, 5, "6", "15", 1);
        session.Exercises[0].Parts[0].TechniqueMaintained = false;
        WorkoutRules.Complete(session);
        session.SyncedAt = DateTimeOffset.UtcNow;
        var week = plan.Days[0].Exercises[0].Weeks[0];
        week.ExistingLoad = "1. Тяга блока\n1) 40 кг × 10\n2) 40 кг × 10\n\n2. Отведения гантелей\n1) 6 кг × 15\n2) 6 кг × 15";
        // Запас не отправляли: оставшаяся в Google старая запись не заменяет локальные пустые поля.
        week.ExistingReserve = "1. Тяга блока\n1) 3\n2) 3\n\n2. Отведения гантелей\n1) 2\n2) 2";
        if (edited)
        {
            week.ExistingLoad = week.ExistingLoad.Replace("40 кг", "42.5 кг");
        }
        var state = new LocalState { Plan = plan, Sessions = [session] };
        var context = new ProgressionContext("test-sheet", 42, 2, 5, Name, 2, 2, "8-12", PartName: "Тяга блока");
        var previous = PreviousWorkoutFinder.Find(state, context)!;
        Assert.Equal(edited ? 42.5m : 40m, previous.Sets[0].Weight);
        Assert.Equal(edited ? (bool?)null : false, previous.TechniqueMaintained);
        Assert.Equal(edited ? 3 : (int?)null, previous.Sets[0].Reserve);
    }

    [Theory]
    [InlineData("10 кг×10/10 кг×10\n12 кг×8")]
    [InlineData("10 кг×10//10 кг×10")]
    [InlineData("10 кг×10/10 кг×10/10 кг×10")]
    public void Ambiguous_compact_rows_are_not_used_for_part_progression(string load)
    {
        var week = new WeekPrescription { Sets = 2, ExistingLoad = load };
        var previous = PreviousWorkoutFinder.FromSheet(week, "Тяга блока", partIndex: 0, partCount: 2);
        Assert.False(previous.Parsed);
        Assert.Empty(previous.Sets);
    }

    [Fact]
    public void Previous_results_and_settings_are_separate_for_each_part_locally_and_from_sheet()
    {
        var plan = Plan();
        var session = Start(plan);
        WorkoutRules.FillRemainingSets(session, 5, "40", "10", 0);
        WorkoutRules.FillRemainingSets(session, 5, "6", "12", 1);
        session.Exercises[0].Parts[0].TechniqueMaintained = false;
        var cell = WorkoutRules.Complete(session).Cells[0];
        var state = new LocalState { Plan = plan, Sessions = [session] };
        var firstContext = new ProgressionContext("test-sheet", 42, 2, 5, Name, 2, 2, "8-12", PartName: "Тяга блока");
        var secondContext = firstContext with { PartName = "Отведения гантелей" };
        var first = PreviousWorkoutFinder.Find(state, firstContext)!;
        var second = PreviousWorkoutFinder.Find(state, secondContext)!;
        Assert.Equal(40, first.Sets[0].Weight);
        Assert.Equal(6, second.Sets[0].Weight);
        Assert.False(first.TechniqueMaintained);
        Assert.Null(second.TechniqueMaintained);
        Assert.NotEqual(firstContext.SettingsKey, secondContext.SettingsKey);

        state.Sessions.Clear();
        plan.Days[0].Exercises[0].Weeks[0].ExistingLoad = cell.Load;
        Assert.Equal(40, PreviousWorkoutFinder.Find(state, firstContext)!.Sets[0].Weight);
        Assert.Equal(6, PreviousWorkoutFinder.Find(state, secondContext)!.Sets[0].Weight);
    }

    [Fact]
    public void Progression_raises_weight_and_repetitions_independently_in_the_same_superset()
    {
        var plan = Plan();
        var session = Start(plan);
        WorkoutRules.FillRemainingSets(session, 5, "40", "12", 0);
        WorkoutRules.FillRemainingSets(session, 5, "6", "10", 1);
        foreach (var entry in session.Exercises[0].Parts)
        {
            entry.TechniqueMaintained = true;
            entry.Sets.ForEach(set => set.Reserve = 2);
        }
        WorkoutRules.Complete(session);
        var state = new LocalState { Plan = plan, Sessions = [session] };
        var firstContext = new ProgressionContext("test-sheet", 42, 2, 5, Name, 2, 2, "8-12", PartName: "Тяга блока");
        var secondContext = firstContext with { PartName = "Отведения гантелей" };
        var first = ProgressionCalculator.Calculate(firstContext, PreviousWorkoutFinder.Find(state, firstContext), new());
        var second = ProgressionCalculator.Calculate(secondContext, PreviousWorkoutFinder.Find(state, secondContext), new());
        Assert.Equal(ProgressionAction.AddWeight, first.Action);
        Assert.All(first.Goals, goal => Assert.Equal(42.5m, goal.Weight));
        Assert.Equal(ProgressionAction.AddRepetitions, second.Action);
        Assert.All(second.Goals, goal => Assert.Equal(6, goal.Weight));
        Assert.All(second.Goals, goal => Assert.Equal(11, goal.Repetitions));
    }

    [Fact]
    public void Different_planned_ranges_are_kept_for_each_part_in_sheet_history()
    {
        var plan = Plan();
        var week = plan.Days[0].Exercises[0].Weeks[0];
        week.Repetitions = "8-12 + 12-20";
        var session = Start(plan);
        Assert.Equal("8-12", session.Exercises[0].Parts[0].PlannedRepetitions);
        Assert.Equal("12-20", session.Exercises[0].Parts[1].PlannedRepetitions);
        WorkoutRules.FillRemainingSets(session, 5, "40", "10", 0);
        WorkoutRules.FillRemainingSets(session, 5, "6", "15", 1);
        week.ExistingLoad = WorkoutRules.Complete(session).Cells[0].Load;
        var context = new ProgressionContext("test-sheet", 42, 2, 5, Name, 2, 2, "12-20", PartName: "Отведения гантелей");
        var previous = PreviousWorkoutFinder.Find(new LocalState { Plan = plan }, context)!;
        Assert.Equal("12-20", previous.Repetitions);
        Assert.Equal(6, previous.Sets[0].Weight);
    }

    [Fact]
    public void Unlabelled_old_results_are_never_assigned_to_a_superset_part()
    {
        var plan = Plan();
        plan.Days[0].Exercises[0].Weeks[0].ExistingLoad = "40 кг × 10\n40 кг × 10";
        var state = new LocalState { Plan = plan };
        var context = new ProgressionContext("test-sheet", 42, 2, 5, Name, 2, 2, "8-12", PartName: "Отведения гантелей");
        var previous = PreviousWorkoutFinder.Find(state, context)!;
        Assert.False(previous.Parsed);
        Assert.Empty(previous.Sets);
        Assert.Contains("40 кг", previous.RawLoad);
    }

    [Fact]
    public void Existing_records_require_explicit_split_when_they_contain_input()
    {
        var session = Start();
        var exercise = session.Exercises[0];
        exercise.Parts.Clear();
        exercise.QuickWeightInput = "30,";
        exercise.Sets[0] = new SetResult { Weight = 30, WeightInput = "30", Repetitions = 8, Done = true };
        SupersetRules.PrepareEmptyEntries(session);
        Assert.Empty(exercise.Parts);
        SupersetRules.Enable(session, 5);
        Assert.Equal("30,", exercise.Parts[0].QuickWeightInput);
        Assert.True(exercise.Parts[0].Sets[0].Done);
        Assert.Equal(30, exercise.Parts[0].Sets[0].Weight);
        Assert.All(exercise.Parts[1].Sets, set => Assert.Null(set.Weight));
        var restored = JsonSerializer.Deserialize<WorkoutSession>(JsonSerializer.Serialize(session))!;
        Assert.Equal(240, restored.Volume);
        Assert.Equal(1, restored.CompletedSets);
    }

    [Fact]
    public void Finished_records_are_not_split_by_migration_or_manual_actions()
    {
        var session = Start();
        session.Exercises[0].Parts.Clear();
        session.FinishedAt = DateTimeOffset.UtcNow;
        SupersetRules.PrepareEmptyEntries(session);
        Assert.Empty(session.Exercises[0].Parts);
        Assert.Throws<InvalidOperationException>(() => SupersetRules.Enable(session, 5));
    }

    [Theory]
    [InlineData("Жим + дроп 20%")]
    [InlineData("Жим или отжимания")]
    [InlineData("Жим СУПЕРСЕТ")]
    [InlineData("Жим + СУПЕРСЕТ")]
    public void Ambiguous_names_are_not_silently_split(string name)
    {
        Assert.Empty(SupersetRules.Names(name));
    }
}
