using WorkoutLog.Core;

namespace WorkoutLog.Tests;

public sealed class PlanAndQueueTests
{
    public static string SampleCsv => """"
        Разминка перед тренировкой,,,,,,,,
        "Грудь, дельты",неделя 1,,,,неделя 2,,,
        ,,,,,,,,
        ,подходы,повторы,нагрузка,запас,подходы,повторы,нагрузка,запас
        "Жим, ""Смит""",2,6-10,,,3,8-12,50,2
        """";

    public static WorkoutPlan Plan()
    {
        return SheetParser.Parse(SampleCsv, "test-sheet", 42);
    }

    public static (WorkoutSession Session, PendingUpload Upload) Completed()
    {
        var plan = Plan();
        var session = WorkoutRules.Start(plan, plan.Days[0], 1);
        foreach (var set in session.Exercises[0].Sets)
        {
            set.Weight = 60.5m;
            set.Repetitions = 10;
            set.Reserve = 2;
            set.Done = true;
        }
        return (session, WorkoutRules.Complete(session));
    }

    [Fact]
    public void Csv_keeps_empty_rows_and_quoted_cells_for_exact_sheet_coordinates()
    {
        var plan = Plan();
        Assert.Equal(2, plan.WeekCount);
        Assert.Equal(2, plan.Days[0].HeaderRow);
        Assert.Equal(5, plan.Days[0].Exercises[0].Row);
        Assert.Equal("Жим, \"Смит\"", plan.Days[0].Exercises[0].Name);
        Assert.Equal(4, plan.Days[0].Exercises[0].Weeks[0].LoadColumn);
        Assert.Equal(8, plan.Days[0].Exercises[0].Weeks[1].LoadColumn);
        Assert.Equal("50", plan.Days[0].Exercises[0].Weeks[1].ExistingLoad);
    }

    [Fact]
    public void Csv_preserves_multiline_cell_and_final_empty_cell()
    {
        var rows = SheetParser.ReadCsv("one,\"two\nlines\",\r\nnext,,");
        Assert.Equal(2, rows.Count);
        Assert.Equal("two\nlines", rows[0][1]);
        Assert.Equal(3, rows[1].Count);
    }

    [Theory]
    [InlineData(1, "A")]
    [InlineData(26, "Z")]
    [InlineData(27, "AA")]
    [InlineData(40, "AN")]
    [InlineData(703, "AAA")]
    public void Sheet_columns_cover_ten_weeks_and_beyond(int index, string expected)
    {
        Assert.Equal(expected, SheetParser.ColumnName(index));
    }

    [Fact]
    public void Parser_refuses_changed_column_order()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SheetParser.Parse(SampleCsv.Replace("нагрузка,запас", "запас,нагрузка"), "test-sheet", 42));
    }

    [Theory]
    [InlineData("https://evil.example/spreadsheets/d/123/edit?gid=42")]
    [InlineData("https://docs.google.com/spreadsheets/d/123/edit")]
    [InlineData("http://docs.google.com/spreadsheets/d/123/edit?gid=42")]
    public void Source_url_must_target_a_specific_google_sheet(string url)
    {
        Assert.Throws<InvalidOperationException>(() => SheetParser.ParseUrl(url));
    }

    [Theory]
    [InlineData("https://docs.google.com/spreadsheets/d/abc_123/edit?gid=42")]
    [InlineData("https://docs.google.com/spreadsheets/d/abc_123/edit#gid=42")]
    public void Source_accepts_query_or_fragment_gid(string url)
    {
        Assert.Equal(("abc_123", 42), SheetParser.ParseUrl(url));
    }

    [Fact]
    public void Completion_freezes_values_without_replacing_planned_repetitions()
    {
        var (session, upload) = Completed();
        Assert.NotNull(session.FinishedAt);
        Assert.Equal("60.5 кг × 10\n60.5 кг × 10", upload.Cells[0].Load);
        Assert.Equal("2 / 2", upload.Cells[0].Reserve);
        Assert.Equal("6-10", upload.Cells[0].PlannedRepetitions);
        Assert.Equal(1210, session.Volume);
        session.Exercises[0].Sets[0].Weight = 70;
        Assert.StartsWith("60.5", upload.Cells[0].Load);
        Assert.Throws<InvalidOperationException>(() => WorkoutRules.Complete(session));
    }

    [Fact]
    public void Completion_requires_done_or_explicitly_skipped_sets()
    {
        var plan = Plan();
        var session = WorkoutRules.Start(plan, plan.Days[0], 1);
        Assert.Throws<InvalidOperationException>(() => WorkoutRules.Complete(session));
        session.Exercises[0].Sets[0] = new SetResult { Weight = 0, Repetitions = 10, Reserve = 0, Done = true };
        Assert.Throws<InvalidOperationException>(() => WorkoutRules.Complete(session));
        session.Exercises[0].Skipped = true;
        Assert.Single(WorkoutRules.Complete(session).Cells);
    }

    [Theory]
    [InlineData(-1, 10, 2)]
    [InlineData(10, 0, 2)]
    [InlineData(10, 10, -1)]
    [InlineData(10, 10, 21)]
    public void Invalid_results_cannot_be_confirmed(decimal weight, int repetitions, int reserve)
    {
        Assert.False(WorkoutRules.IsValid(new SetResult
        {
            Weight = weight,
            Repetitions = repetitions,
            Reserve = reserve
        }));
    }

    [Fact]
    public void Lost_response_is_recognized_without_a_duplicate_write()
    {
        var (_, upload) = Completed();
        var remote = Plan();
        var week = remote.Days[0].Exercises[0].Weeks[0];
        week.ExistingLoad = upload.Cells[0].Load;
        week.ExistingReserve = upload.Cells[0].Reserve;
        Assert.Equal(ReconciliationResult.AlreadyApplied, UploadReconciliation.Evaluate(upload, remote));
    }

    [Fact]
    public void Unchanged_remote_cells_are_safe_to_write()
    {
        var (_, upload) = Completed();
        Assert.Equal(ReconciliationResult.SafeToWrite, UploadReconciliation.Evaluate(upload, Plan()));
    }

    [Fact]
    public void External_edits_stop_upload()
    {
        var (_, upload) = Completed();
        var remote = Plan();
        remote.Days[0].Exercises[0].Weeks[0].ExistingLoad = "Изменил тренер";
        Assert.Equal(ReconciliationResult.Conflict, UploadReconciliation.Evaluate(upload, remote));
    }

    [Fact]
    public void Moved_rows_stop_upload_even_when_names_are_identical()
    {
        var (_, upload) = Completed();
        var remote = Plan();
        remote.Days[0].Exercises[0].Row++;
        Assert.Equal(ReconciliationResult.Conflict, UploadReconciliation.Evaluate(upload, remote));
    }

    [Fact]
    public void Changed_plan_and_wrong_destination_are_conflicts()
    {
        var (_, upload) = Completed();
        var remote = Plan();
        remote.Days[0].Exercises[0].Weeks[0].Repetitions = "12-15";
        Assert.Equal(ReconciliationResult.Conflict, UploadReconciliation.Evaluate(upload, remote));
        remote = Plan();
        remote.SheetId = 43;
        Assert.Equal(ReconciliationResult.Conflict, UploadReconciliation.Evaluate(upload, remote));
    }
}
