using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.JSInterop;
using WorkoutLog.Core;
using WorkoutLog.Web.Services;

namespace WorkoutLog.Tests;

public sealed class GoogleGatewayTests
{
    [Fact]
    public async Task Superset_writes_one_cell_and_retries_without_duplicate_or_reserve_changes()
    {
        var handler = new SheetHandler { LoseFirstWriteResponse = true };
        handler.Rows[4][0] = SupersetTests.Name;
        handler.Rows[4][4] = "=SUM(A1:A2)";
        var plan = SheetParser.ParseRows(handler.Rows, "test-sheet", 42);
        var session = WorkoutRules.Start(plan, plan.Days[0], 1);
        WorkoutRules.FillRemainingSets(session, 5, "40", "10", 0);
        WorkoutRules.FillRemainingSets(session, 5, "6", "15", 1);
        var upload = WorkoutRules.Complete(session);
        var gateway = CreateGateway(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => gateway.UploadAsync(upload));
        await gateway.UploadAsync(upload);
        Assert.Equal(1, handler.Writes);
        var data = handler.LastBody!.RootElement.GetProperty("data");
        Assert.Equal(1, data.GetArrayLength());
        Assert.Equal("'План ''силовой'''!D5", data[0].GetProperty("range").GetString());
        Assert.Equal(1, data[0].GetProperty("values")[0].GetArrayLength());
        Assert.Equal("=SUM(A1:A2)", handler.Rows[4][4]);
        Assert.Contains("2. Отведения гантелей", handler.Rows[4][3]);
    }

    [Fact]
    public async Task Upload_writes_only_load_and_reserve_in_the_selected_sheet()
    {
        var handler = new SheetHandler();
        var gateway = CreateGateway(handler);
        var (_, upload) = PlanAndQueueTests.Completed();
        await gateway.UploadAsync(upload);

        Assert.Equal(1, handler.Writes);
        Assert.Equal("RAW", handler.LastBody!.RootElement.GetProperty("valueInputOption").GetString());
        Assert.Equal("'План ''силовой'''!D5:E5",
            handler.LastBody.RootElement.GetProperty("data")[0].GetProperty("range").GetString());
        Assert.Equal(2, handler.ValueReads);
    }

    [Fact]
    public async Task Lost_write_response_reconciles_on_the_next_attempt()
    {
        var handler = new SheetHandler { LoseFirstWriteResponse = true };
        var gateway = CreateGateway(handler);
        var (_, upload) = PlanAndQueueTests.Completed();

        await Assert.ThrowsAsync<HttpRequestException>(() => gateway.UploadAsync(upload));
        await gateway.UploadAsync(upload);
        Assert.Equal(1, handler.Writes);
    }

    [Fact]
    public async Task Conflict_never_sends_a_write()
    {
        var handler = new SheetHandler();
        handler.Rows[4][3] = "Тренер изменил результат";
        var gateway = CreateGateway(handler);
        var (_, upload) = PlanAndQueueTests.Completed();

        await Assert.ThrowsAsync<SheetConflictException>(() => gateway.UploadAsync(upload));
        Assert.Equal(0, handler.Writes);
    }

    [Fact]
    public async Task Unrecognizable_sheet_structure_is_a_conflict_not_a_network_retry()
    {
        var handler = new SheetHandler();
        handler.Rows[3][3] = "Изменённый заголовок";
        var (_, upload) = PlanAndQueueTests.Completed();
        await Assert.ThrowsAsync<SheetConflictException>(() => CreateGateway(handler).UploadAsync(upload));
        Assert.Equal(0, handler.Writes);
    }

    [Fact]
    public async Task Expired_token_does_not_send_any_request()
    {
        var handler = new SheetHandler();
        var gateway = new GoogleSheetsGateway(new HttpClient(handler), new FakeJavascript(null));
        var (_, upload) = PlanAndQueueTests.Completed();
        await Assert.ThrowsAsync<GoogleSignInRequiredException>(() => gateway.UploadAsync(upload));
        Assert.Equal(0, handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, typeof(GoogleSignInRequiredException))]
    [InlineData(HttpStatusCode.Forbidden, typeof(GooglePermissionException))]
    [InlineData(HttpStatusCode.TooManyRequests, typeof(HttpRequestException))]
    [InlineData(HttpStatusCode.ServiceUnavailable, typeof(HttpRequestException))]
    public async Task Api_errors_preserve_the_distinction_between_auth_and_network(
        HttpStatusCode status, Type exceptionType)
    {
        var handler = new SheetHandler { ErrorStatus = status };
        var (_, upload) = PlanAndQueueTests.Completed();
        var exception = await Record.ExceptionAsync(() => CreateGateway(handler).UploadAsync(upload));
        Assert.IsType(exceptionType, exception);
        Assert.Equal(0, handler.Writes);
    }

    [Theory]
    [InlineData("2 / 1")]
    [InlineData("=SUM(A1:A2)")]
    public async Task Empty_reserve_never_writes_clears_or_conflicts_with_google_reserve(string existingReserve)
    {
        var handler = new SheetHandler { LoseFirstWriteResponse = true };
        handler.Rows[4][4] = existingReserve;
        var plan = PlanAndQueueTests.Plan();
        var session = WorkoutRules.Start(plan, plan.Days[0], 1);
        WorkoutRules.FillRemainingSets(session, 5, "60", "10");
        var upload = WorkoutRules.Complete(session);
        Assert.False(upload.Cells[0].WritesReserve);
        var gateway = CreateGateway(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => gateway.UploadAsync(upload));
        await gateway.UploadAsync(upload);

        Assert.Equal(1, handler.Writes);
        var data = handler.LastBody!.RootElement.GetProperty("data")[0];
        Assert.Equal("'План ''силовой'''!D5", data.GetProperty("range").GetString());
        Assert.Equal(1, data.GetProperty("values")[0].GetArrayLength());
        Assert.Equal(existingReserve, handler.Rows[4][4]);
    }

    private static GoogleSheetsGateway CreateGateway(SheetHandler handler)
    {
        return new GoogleSheetsGateway(new HttpClient(handler), new FakeJavascript("test-token"));
    }

    private sealed class FakeJavascript(string? token) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            return ValueTask.FromResult((TValue)(object?)token!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            return InvokeAsync<TValue>(identifier, args);
        }
    }

    private sealed class SheetHandler : HttpMessageHandler
    {
        public List<List<string>> Rows { get; } = SheetParser.ReadCsv(PlanAndQueueTests.SampleCsv);
        public int Requests { get; private set; }
        public int Writes { get; private set; }
        public int ValueReads { get; private set; }
        public bool LoseFirstWriteResponse { get; init; }
        public HttpStatusCode? ErrorStatus { get; init; }
        public JsonDocument? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (ErrorStatus is { } status)
            {
                return new HttpResponseMessage(status) { Content = new StringContent("{}") };
            }
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            if (request.Method == HttpMethod.Post)
            {
                Writes++;
                LastBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var values = LastBody.RootElement.GetProperty("data")[0].GetProperty("values")[0];
                Rows[4][3] = values[0].GetString()!;
                if (values.GetArrayLength() > 1)
                {
                    Rows[4][4] = values[1].GetString()!;
                }
                if (LoseFirstWriteResponse && Writes == 1)
                {
                    throw new HttpRequestException("Connection lost after server commit.");
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { totalUpdatedCells = 2 }) };
            }
            if (request.RequestUri!.AbsolutePath.Contains("/values/"))
            {
                ValueReads++;
                Assert.Contains("valueRenderOption=FORMULA", request.RequestUri.Query);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { values = Rows }) };
            }
            var metadata = new
            {
                sheets = new[]
                {
                    new
                    {
                        properties = new
                        {
                            sheetId = 42,
                            title = "План 'силовой'",
                            gridProperties = new { rowCount = 1000, columnCount = 50 }
                        }
                    }
                }
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(metadata) };
        }
    }
}
