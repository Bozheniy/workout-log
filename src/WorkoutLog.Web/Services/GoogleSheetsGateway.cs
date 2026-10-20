using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.JSInterop;
using WorkoutLog.Core;

namespace WorkoutLog.Web.Services;

public sealed class GoogleSignInRequiredException : Exception;
public sealed class GooglePermissionException : Exception;
public sealed class SheetConflictException : Exception;

public sealed class GoogleSheetsGateway(HttpClient http, IJSRuntime javascript)
{
    public async Task<WorkoutPlan> DownloadAsync(string url)
    {
        var (spreadsheetId, sheetId) = SheetParser.ParseUrl(url);
        var token = await javascript.InvokeAsync<string?>("workout.auth.token");
        if (!string.IsNullOrEmpty(token))
        {
            var authenticated = await ReadAuthenticatedAsync(spreadsheetId, sheetId);
            return authenticated.Plan;
        }

        // CSV export сохраняет номера строк, включая пустые строки и заголовки.
        // Для закрытой таблицы потребуется вход, но публичную можно читать сразу.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var exportUrl = $"https://docs.google.com/spreadsheets/d/{spreadsheetId}/export?format=csv&gid={sheetId}";
        using var response = await http.GetAsync(exportUrl, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Таблица недоступна. Проверьте ссылку и войдите в Google для закрытого плана.");
        }
        var csv = await response.Content.ReadAsStringAsync(timeout.Token);
        if (csv.TrimStart().StartsWith('<'))
        {
            throw new InvalidOperationException("Google запрашивает вход. Подключите Google в настройках.");
        }
        return SheetParser.Parse(csv, spreadsheetId, sheetId);
    }

    public async Task UploadAsync(PendingUpload upload)
    {
        var snapshot = await ReadUploadSnapshotAsync(upload);
        var decision = UploadReconciliation.Evaluate(upload, snapshot.Plan);
        if (decision == ReconciliationResult.Conflict)
        {
            throw new SheetConflictException();
        }
        if (decision == ReconciliationResult.AlreadyApplied)
        {
            return;
        }

        var quotedTitle = "'" + snapshot.Title.Replace("'", "''") + "'";
        var values = upload.Cells.Select(cell => new
        {
            range = $"{quotedTitle}!{SheetParser.ColumnName(cell.LoadColumn)}{cell.Row}:" +
                $"{SheetParser.ColumnName(cell.LoadColumn + 1)}{cell.Row}",
            values = new[] { new[] { cell.Load, cell.Reserve } }
        });

        using var request = await CreateRequestAsync(HttpMethod.Post,
            $"https://sheets.googleapis.com/v4/spreadsheets/{upload.SpreadsheetId}/values:batchUpdate");
        // RAW не позволяет превратить пользовательский текст в формулу Google.
        request.Content = JsonContent.Create(new { valueInputOption = "RAW", data = values });
        using var response = await SendAsync(request);

        // Дополнительное чтение подтверждает содержимое; один HTTP 200 недостаточен.
        var confirmation = await ReadUploadSnapshotAsync(upload);
        if (UploadReconciliation.Evaluate(upload, confirmation.Plan) != ReconciliationResult.AlreadyApplied)
        {
            throw new SheetConflictException();
        }
    }

    private async Task<(WorkoutPlan Plan, string Title)> ReadUploadSnapshotAsync(PendingUpload upload)
    {
        try
        {
            return await ReadAuthenticatedAsync(upload.SpreadsheetId, upload.SheetId);
        }
        catch (InvalidOperationException)
        {
            // Удалённый лист или изменившийся формат — конфликт, а не сбой сети.
            throw new SheetConflictException();
        }
    }

    private async Task<(WorkoutPlan Plan, string Title)> ReadAuthenticatedAsync(string spreadsheetId, int sheetId)
    {
        using var metadataRequest = await CreateRequestAsync(HttpMethod.Get,
            $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}" +
            "?fields=sheets(properties(sheetId,title,gridProperties))");
        using var metadataResponse = await SendAsync(metadataRequest);
        using var metadata = JsonDocument.Parse(await metadataResponse.Content.ReadAsStringAsync());
        var sheet = metadata.RootElement.GetProperty("sheets").EnumerateArray()
            .Select(item => item.GetProperty("properties"))
            .SingleOrDefault(item => item.GetProperty("sheetId").GetInt32() == sheetId);
        if (sheet.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException("Лист с указанным gid больше не существует.");
        }

        var title = sheet.GetProperty("title").GetString()!;
        var grid = sheet.GetProperty("gridProperties");
        var rowsCount = Math.Min(grid.GetProperty("rowCount").GetInt32(), 5000);
        var columnsCount = Math.Min(grid.GetProperty("columnCount").GetInt32(), 500);
        var range = "'" + title.Replace("'", "''") + "'!A1:" +
            SheetParser.ColumnName(columnsCount) + rowsCount;
        using var valuesRequest = await CreateRequestAsync(HttpMethod.Get,
            $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}/values/{Uri.EscapeDataString(range)}" +
            "?valueRenderOption=FORMULA");
        using var valuesResponse = await SendAsync(valuesRequest);
        using var values = JsonDocument.Parse(await valuesResponse.Content.ReadAsStringAsync());
        var rows = new List<List<string>>();
        if (values.RootElement.TryGetProperty("values", out var array))
        {
            foreach (var row in array.EnumerateArray())
            {
                rows.Add(row.EnumerateArray().Select(cell =>
                    cell.ValueKind == JsonValueKind.String ? cell.GetString()! : cell.ToString()).ToList());
            }
        }
        return (SheetParser.ParseRows(rows, spreadsheetId, sheetId), title);
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string url)
    {
        var token = await javascript.InvokeAsync<string?>("workout.auth.token");
        if (string.IsNullOrEmpty(token))
        {
            throw new GoogleSignInRequiredException();
        }
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var response = await http.SendAsync(request, timeout.Token);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            throw new GoogleSignInRequiredException();
        }
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            // Квоту Google иногда обозначает HTTP 403: такую ошибку нужно повторить.
            var body = await response.Content.ReadAsStringAsync();
            response.Dispose();
            if (body.Contains("rateLimitExceeded", StringComparison.OrdinalIgnoreCase)
                || body.Contains("userRateLimitExceeded", StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpRequestException("Достигнут лимит запросов Google. Повторим позже.");
            }
            throw new GooglePermissionException();
        }
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"Google временно недоступен (HTTP {status}).");
        }
        return response;
    }
}
