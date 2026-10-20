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
                Rows[4][4] = values[1].GetString()!;
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
