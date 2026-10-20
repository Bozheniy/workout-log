using WorkoutLog.Core;

namespace WorkoutLog.Tests;

public sealed class ProgressionTests
{
    private static ProgressionContext Context(int week = 2) =>
        new("sheet", 42, 10, 12, "Жим", week, 3, "8–12");

    private static PreviousExercise Previous(int[] repetitions, int?[]? reserves = null, bool? technique = true)
    {
        reserves ??= [2, 1, 0];
        return new PreviousExercise
        {
            Week = 1,
            PlannedSets = 3,
            Repetitions = "8-12",
            Parsed = true,
            Complete = true,
            TechniqueMaintained = technique,
            Sets = repetitions.Select((reps, index) =>
                new PreviousSet(index + 1, 60, reps, reserves[index])).ToList()
        };
    }

    [Fact]
    public void Adds_repetitions_before_weight_and_caps_each_set_at_range_top()
    {
        var advice = ProgressionCalculator.Calculate(Context(), Previous([12, 10, 8]), new());
        Assert.Equal(ProgressionAction.AddRepetitions, advice.Action);
        Assert.Equal([12, 11, 9], advice.Goals.Select(goal => goal.Repetitions));
        Assert.All(advice.Goals, goal => Assert.Equal(60, goal.Weight));
    }

    [Fact]
    public void Weight_increases_only_after_all_sets_hit_top_with_target_reserve()
    {
        var advice = ProgressionCalculator.Calculate(Context(), Previous([12, 12, 12]), new());
        Assert.Equal(ProgressionAction.AddWeight, advice.Action);
        Assert.All(advice.Goals, goal =>
        {
            Assert.Equal(62.5m, goal.Weight);
            Assert.Equal(8, goal.Repetitions);
        });
        Assert.Equal([2, 1, 0], advice.Goals.Select(goal => goal.Reserve));
    }

    [Fact]
    public void Low_reserve_prevents_weight_increase_and_reduces_repetitions()
    {
        var advice = ProgressionCalculator.Calculate(Context(), Previous([12, 12, 12], [0, 1, 0]), new());
        Assert.Equal(ProgressionAction.Adjust, advice.Action);
        Assert.Equal(10, advice.Goals[0].Repetitions);
        Assert.All(advice.Goals, goal => Assert.Equal(60, goal.Weight));
    }

    [Fact]
    public void Custom_step_is_used_without_rounding_or_replacing_recorded_weights()
    {
        var advice = ProgressionCalculator.Calculate(Context(), Previous([12, 12, 12]),
            new ExerciseProgressionSettings { WeightStep = 1.25m });
        Assert.All(advice.Goals, goal => Assert.Equal(61.25m, goal.Weight));
    }

    [Fact]
    public void Bodyweight_is_not_automatically_converted_to_external_weight()
    {
        var previous = Previous([12, 12, 12]);
        for (var index = 0; index < previous.Sets.Count; index++)
        {
            previous.Sets[index] = previous.Sets[index] with { Weight = 0 };
        }
        var advice = ProgressionCalculator.Calculate(Context(), previous, new());
        Assert.Equal(ProgressionAction.Hold, advice.Action);
        Assert.All(advice.Goals, goal => Assert.Equal(0, goal.Weight));
    }

    [Fact]
    public void Excessive_decrement_does_not_create_negative_or_zero_weight()
    {
        var advice = ProgressionCalculator.Calculate(Context(), Previous([6, 6, 6]),
            new ExerciseProgressionSettings { WeightStep = 100 });
        Assert.Empty(advice.Goals);
        Assert.Equal(ProgressionAction.NeedData, advice.Action);
    }

    [Fact]
    public void Load_decreases_when_minimum_cannot_be_reached_with_target_reserve()
    {
        var advice = ProgressionCalculator.Calculate(Context(), Previous([8, 7, 6], [0, 0, 0]), new());
        Assert.Equal(ProgressionAction.Adjust, advice.Action);
        Assert.All(advice.Goals, goal => Assert.Equal(new SetGoal(goal.Number, 57.5m, 8, goal.Reserve), goal));
    }

    [Fact]
    public void Low_repetitions_with_plenty_of_reserve_do_not_trigger_unnecessary_weight_reduction()
    {
        var advice = ProgressionCalculator.Calculate(Context(), Previous([6, 8, 9], [5, 1, 0]), new());
        Assert.Equal(60, advice.Goals[0].Weight);
        Assert.Equal(8, advice.Goals[0].Repetitions);
    }

    [Fact]
    public void Technique_failure_overrides_successful_top_range_performance()
    {
        var advice = ProgressionCalculator.Calculate(Context(), Previous([12, 12, 12], technique: false), new());
        Assert.Equal(ProgressionAction.Adjust, advice.Action);
        Assert.All(advice.Goals, goal => Assert.Equal(57.5m, goal.Weight));
    }

    [Fact]
    public void Old_data_without_technique_rating_produces_an_explicitly_conditional_goal()
    {
        var advice = ProgressionCalculator.Calculate(Context(), Previous([12, 12, 12], technique: null), new());
        Assert.True(advice.TechniqueUnknown);
    }

    [Fact]
    public void Missing_reserve_does_not_turn_into_zero_or_authorize_progression()
    {
        var advice = ProgressionCalculator.Calculate(Context(), Previous([12, 12, 12], [2, null, 0]), new());
        Assert.Equal(ProgressionAction.NeedData, advice.Action);
        Assert.Empty(advice.Goals);
    }

    [Theory]
    [InlineData("6-10", 3)]
    [InlineData("8-12", 4)]
    public void Changed_prescription_requires_a_new_baseline(string repetitions, int sets)
    {
        var context = Context() with { Repetitions = repetitions, SetCount = sets };
        Assert.Equal(ProgressionAction.PlanChanged,
            ProgressionCalculator.Calculate(context, Previous([12, 12, 12]), new()).Action);
    }

    [Fact]
    public void First_workout_has_no_invented_weight_and_uses_lower_boundary()
    {
        var advice = ProgressionCalculator.Calculate(Context(), null, new());
        Assert.Equal(ProgressionAction.FirstWorkout, advice.Action);
        Assert.All(advice.Goals, goal =>
        {
            Assert.Null(goal.Weight);
            Assert.Equal(8, goal.Repetitions);
        });
    }

    [Theory]
    [InlineData("8–12", 8, 12)]
    [InlineData(" 8 — 12 ", 8, 12)]
    [InlineData("10", 10, 10)]
    public void Range_parser_accepts_common_plan_formats(string text, int minimum, int maximum)
    {
        Assert.Equal((minimum, maximum), ProgressionCalculator.ParseRange(text));
    }

    [Theory]
    [InlineData("12-8")]
    [InlineData("8-12 + дроп 20")]
    [InlineData("до отказа")]
    [InlineData("0-12")]
    public void Unclear_or_invalid_ranges_are_not_guessed(string text)
    {
        Assert.Null(ProgressionCalculator.ParseRange(text));
    }

    [Fact]
    public void Four_sets_keep_nonzero_reserve_before_last_set()
    {
        var settings = new ExerciseProgressionSettings();
        Assert.Equal([2, 1, 1, 0], Enumerable.Range(0, 4).Select(index => settings.ReserveFor(index, 4)));
        Assert.Equal(0, settings.ReserveFor(0, 1));
    }

    [Fact]
    public void Sheet_results_preserve_decimal_weights_repetitions_and_reserves()
    {
        var previous = PreviousWorkoutFinder.FromSheet(new WeekPrescription
        {
            Number = 1, Sets = 3, Repetitions = "8-12",
            ExistingLoad = "60,5 кг × 12\n60.5x11\n60.5 х 10",
            ExistingReserve = "2 / 1 / 0"
        });
        Assert.True(previous.Complete);
        Assert.All(previous.Sets, set => Assert.Equal(60.5m, set.Weight));
        Assert.Equal([12, 11, 10], previous.Sets.Select(set => set.Repetitions));
    }

    [Fact]
    public void A_single_reserve_value_is_not_copied_to_multiple_sets()
    {
        var previous = PreviousWorkoutFinder.FromSheet(new WeekPrescription
        {
            Sets = 3, ExistingLoad = "60x12;60x12;60x12", ExistingReserve = "2"
        });
        Assert.All(previous.Sets, set => Assert.Null(set.Reserve));
    }

    [Fact]
    public void Partial_sheet_results_never_authorize_weight_increase()
    {
        var previous = PreviousWorkoutFinder.FromSheet(new WeekPrescription
        {
            Sets = 3, Repetitions = "8-12", ExistingLoad = "60x12;60x12", ExistingReserve = "2/1"
        });
        Assert.False(previous.Complete);
        Assert.Empty(ProgressionCalculator.Calculate(Context(), previous, new()).Goals);
    }

    [Fact]
    public void Freeform_history_is_retained_without_fabricated_numbers()
    {
        var previous = PreviousWorkoutFinder.FromSheet(new WeekPrescription
        {
            Sets = 3, ExistingLoad = "60 кг, тяжело", ExistingReserve = "почти отказ"
        });
        Assert.False(previous.Parsed);
        Assert.Equal("60 кг, тяжело", previous.RawLoad);
        Assert.Empty(previous.Sets);
    }

    [Fact]
    public void Finds_unsynced_local_history_offline_and_excludes_current_or_future_weeks()
    {
        var state = new LocalState { Sessions = [Session(1), Session(2), Session(3)] };
        var previous = PreviousWorkoutFinder.Find(state, Context());
        Assert.Equal(1, previous!.Week);
        Assert.Contains("не отправлено", previous.Source);
    }

    [Fact]
    public void Other_sheet_day_row_or_exercise_name_cannot_supply_history()
    {
        var state = new LocalState { Sessions = [Session(1)] };
        Assert.Null(PreviousWorkoutFinder.Find(state, Context() with { SheetId = 99 }));
        Assert.Null(PreviousWorkoutFinder.Find(state, Context() with { DayRow = 99 }));
        Assert.Null(PreviousWorkoutFinder.Find(state, Context() with { ExerciseRow = 99 }));
        Assert.Null(PreviousWorkoutFinder.Find(state, Context() with { ExerciseName = "Другой жим" }));
    }

    [Fact]
    public void Unfinished_and_later_completed_sessions_are_excluded()
    {
        var session = Session(1);
        var state = new LocalState { Sessions = [session] };
        Assert.Null(PreviousWorkoutFinder.Find(state, Context() with { StartedAt = session.FinishedAt!.Value.AddMinutes(-1) }));
        session.FinishedAt = null;
        Assert.Null(PreviousWorkoutFinder.Find(state, Context()));
    }

    [Fact]
    public void Nearest_populated_sheet_week_beats_older_local_history()
    {
        var state = StateWithSheet();
        state.Sessions.Add(Session(1));
        Assert.Equal(2, PreviousWorkoutFinder.Find(state, Context(3))!.Week);
    }

    [Fact]
    public void Unsynced_local_wins_but_remote_correction_of_synced_result_is_respected()
    {
        var state = StateWithSheet();
        var session = Session(2);
        state.Sessions.Add(session);
        Assert.Equal(60, PreviousWorkoutFinder.Find(state, Context(3))!.Sets[0].Weight);
        session.SyncedAt = DateTimeOffset.UtcNow;
        Assert.Equal(65, PreviousWorkoutFinder.Find(state, Context(3))!.Sets[0].Weight);
    }

    [Fact]
    public void Partial_local_workout_keeps_set_numbers_and_does_not_hide_behind_older_complete_data()
    {
        var session = Session(2);
        session.Exercises[0].Sets[0].Done = false;
        session.Exercises[0].Skipped = true;
        var state = new LocalState { Sessions = [Session(1), session] };
        var previous = PreviousWorkoutFinder.Find(state, Context(3))!;
        Assert.Equal(2, previous.Week);
        Assert.Equal(2, previous.Sets[0].Number);
        Assert.False(previous.Complete);
        Assert.Empty(ProgressionCalculator.Calculate(Context(3), previous, new()).Goals);
    }

    private static WorkoutSession Session(int week)
    {
        return new WorkoutSession
        {
            SpreadsheetId = "sheet", SheetId = 42, DayRow = 10, Week = week,
            FinishedAt = DateTimeOffset.UtcNow.AddDays(-2),
            Exercises = [new ExerciseResult
            {
                Row = 12, Name = "Жим", PlannedRepetitions = "8-12",
                Sets = Enumerable.Range(0, 3).Select(index => new SetResult
                {
                    Weight = 60, Repetitions = 12, Reserve = 2 - index, Done = true
                }).ToList()
            }]
        };
    }

    private static LocalState StateWithSheet()
    {
        return new LocalState
        {
            Plan = new WorkoutPlan
            {
                SpreadsheetId = "sheet", SheetId = 42,
                Days = [new TrainingDay
                {
                    HeaderRow = 10,
                    Exercises = [new PlannedExercise
                    {
                        Row = 12, Name = "Жим",
                        Weeks = [new WeekPrescription
                        {
                            Number = 2, Sets = 3, Repetitions = "8-12",
                            ExistingLoad = "65 кг × 12\n65 кг × 12\n65 кг × 12", ExistingReserve = "2 / 1 / 0"
                        }]
                    }]
                }]
            }
        };
    }
}
