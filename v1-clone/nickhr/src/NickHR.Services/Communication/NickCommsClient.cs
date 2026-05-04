using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NickHR.Core.Interfaces;

namespace NickHR.Services.Communication;

/// <summary>
/// HTTP client for the NickComms.Gateway service. Reads BaseUrl/ApiKey from
/// configuration (NickComms section, with NICKCOMMS_BASE_URL / NICKCOMMS_API_KEY_NICKHR
/// env var fallbacks).
/// </summary>
public class NickCommsClient : INickCommsClient
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<NickCommsClient> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public NickCommsClient(HttpClient http, IConfiguration config, ILogger<NickCommsClient> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    public async Task<NickCommsEmailResult> SendEmailAsync(
        string to, string subject, string htmlBody, bool isHtml = true,
        IEnumerable<NickCommsAttachment>? attachments = null,
        string? clientReference = null,
        CancellationToken ct = default)
    {
        if (!Configure(out var error))
            return new NickCommsEmailResult { Success = false, ErrorMessage = error };

        var payload = new
        {
            to,
            subject,
            body = htmlBody,
            isHtml,
            clientReference,
            attachments = attachments?.Select(a => new { filename = a.Filename, contentBase64 = a.ContentBase64, contentType = a.ContentType })
        };

        try
        {
            using var resp = await _http.PostAsJsonAsync("/api/email/send", payload, JsonOpts, ct);
            return await ParseEmailResponseAsync(resp, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NickComms email send failed for {To}", to);
            return new NickCommsEmailResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<NickCommsEmailResult> SendBulkEmailAsync(
        IEnumerable<string> recipients, string subject, string htmlBody, bool isHtml = true,
        IEnumerable<NickCommsAttachment>? attachments = null,
        CancellationToken ct = default)
    {
        if (!Configure(out var error))
            return new NickCommsEmailResult { Success = false, ErrorMessage = error };

        var payload = new
        {
            recipients = recipients.Select(e => new { email = e }),
            subject,
            body = htmlBody,
            isHtml,
            attachments = attachments?.Select(a => new { filename = a.Filename, contentBase64 = a.ContentBase64, contentType = a.ContentType })
        };

        try
        {
            using var resp = await _http.PostAsJsonAsync("/api/email/bulk", payload, JsonOpts, ct);
            return await ParseEmailResponseAsync(resp, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NickComms bulk email send failed");
            return new NickCommsEmailResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<NickCommsSmsResult> SendSmsAsync(string phoneNumber, string message, string? clientReference = null, CancellationToken ct = default)
    {
        if (!Configure(out var error))
            return new NickCommsSmsResult { Success = false, ErrorMessage = error };

        var payload = new { to = phoneNumber, content = message, clientReference };

        try
        {
            using var resp = await _http.PostAsJsonAsync("/api/sms/send", payload, JsonOpts, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return new NickCommsSmsResult { Success = false, ErrorMessage = $"{(int)resp.StatusCode}: {Truncate(body)}" };

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var result = new NickCommsSmsResult { Success = true };
            if (root.TryGetProperty("id", out var idEl) && idEl.TryGetGuid(out var id)) result.MessageId = id;
            if (root.TryGetProperty("status", out var sEl)) result.Status = sEl.GetString();
            if (root.TryGetProperty("rate", out var rEl) && rEl.ValueKind == JsonValueKind.Number) result.Rate = rEl.GetDecimal();
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NickComms SMS send failed for {Phone}", phoneNumber);
            return new NickCommsSmsResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<NickCommsHistoryPage> GetHistoryAsync(NickCommsHistoryQuery query, CancellationToken ct = default)
    {
        if (!Configure(out _)) return new NickCommsHistoryPage();

        var qs = new List<string> { $"page={query.Page}", $"pageSize={query.PageSize}" };
        if (!string.IsNullOrEmpty(query.Channel)) qs.Add($"channel={Uri.EscapeDataString(query.Channel)}");
        if (!string.IsNullOrEmpty(query.ClientApp)) qs.Add($"clientApp={Uri.EscapeDataString(query.ClientApp)}");
        if (!string.IsNullOrEmpty(query.Recipient)) qs.Add($"recipient={Uri.EscapeDataString(query.Recipient)}");
        if (query.FromDate.HasValue) qs.Add($"fromDate={Uri.EscapeDataString(query.FromDate.Value.ToString("o"))}");
        if (query.ToDate.HasValue) qs.Add($"toDate={Uri.EscapeDataString(query.ToDate.Value.ToString("o"))}");

        try
        {
            return await _http.GetFromJsonAsync<NickCommsHistoryPage>("/api/messages/history?" + string.Join("&", qs), JsonOpts, ct)
                   ?? new NickCommsHistoryPage();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NickComms history fetch failed");
            return new NickCommsHistoryPage();
        }
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (!Configure(out _)) return false;
        try
        {
            using var resp = await _http.GetAsync("/api/health", ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static async Task<NickCommsEmailResult> ParseEmailResponseAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return new NickCommsEmailResult { Success = false, ErrorMessage = $"{(int)resp.StatusCode}: {Truncate(body)}" };

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var result = new NickCommsEmailResult { Success = true };
            if (root.TryGetProperty("id", out var idEl) && idEl.TryGetGuid(out var id)) result.MessageId = id;
            if (root.TryGetProperty("batchId", out var bEl) && bEl.TryGetGuid(out var bid)) result.BatchId = bid;
            if (root.TryGetProperty("acceptedCount", out var ac) && ac.TryGetInt32(out var n)) result.AcceptedCount = n;
            return result;
        }
        catch
        {
            return new NickCommsEmailResult { Success = true };
        }
    }

    private bool Configure(out string? error)
    {
        // Env var takes precedence over appsettings so deployments can rotate
        // secrets without redeploying config files. Placeholder strings of the
        // form "***...***" in appsettings are also treated as unset.
        var baseUrl = Environment.GetEnvironmentVariable("NICKCOMMS_BASE_URL");
        if (IsUnset(baseUrl)) baseUrl = _config["NickComms:BaseUrl"];
        if (IsUnset(baseUrl))
        {
            error = "NickComms BaseUrl not configured.";
            return false;
        }

        var apiKey = Environment.GetEnvironmentVariable("NICKCOMMS_API_KEY_NICKHR");
        if (IsUnset(apiKey)) apiKey = _config["NickComms:ApiKey"];
        if (IsUnset(apiKey))
        {
            error = "NickComms ApiKey not configured.";
            return false;
        }

        if (_http.BaseAddress?.ToString().TrimEnd('/') != baseUrl.TrimEnd('/'))
            _http.BaseAddress = new Uri(baseUrl);

        _http.DefaultRequestHeaders.Remove("X-Api-Key");
        _http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

        error = null;
        return true;
    }

    private static string Truncate(string s, int max = 200)
        => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s.Substring(0, max) + "…");

    /// <summary>
    /// Treats null/whitespace AND placeholder strings of the form "***...***"
    /// as "not configured" so deploy templates don't accidentally become real values.
    /// </summary>
    private static bool IsUnset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var trimmed = value!.Trim();
        return trimmed.StartsWith("***") && trimmed.EndsWith("***");
    }
}
