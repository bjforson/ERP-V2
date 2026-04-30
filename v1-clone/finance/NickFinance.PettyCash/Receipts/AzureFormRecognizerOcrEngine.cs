using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NickFinance.PettyCash.Receipts;

/// <summary>
/// Azure AI Document Intelligence (formerly Form Recognizer) backed
/// receipt OCR. Uses the prebuilt-receipt model — Microsoft trains it
/// on grocery / restaurant / fuel receipts which is exactly what
/// Ghana custodians submit for petty-cash claims (transport, snacks,
/// fuel, office supplies).
///
/// <para>
/// Configuration:
/// <list type="bullet">
///   <item><c>NickFinance:Ocr:Endpoint</c> — e.g. <c>https://nickerp-finance-di.cognitiveservices.azure.com/</c>.</item>
///   <item><c>AZURE_DOCUMENT_INTELLIGENCE_KEY</c> — primary key from Azure portal (machine env var).</item>
/// </list>
/// </para>
///
/// <para>
/// Implemented as a thin REST client over Document Intelligence's 2024-11-30
/// API so it doesn't pull in the full Azure.AI.DocumentIntelligence SDK
/// (avoids transitive dependency on Azure.Core, Azure.Identity, etc. — keeps
/// the image lean). When the SDK becomes a net win (e.g. for token-based
/// auth) the implementation can swap without changing
/// <see cref="IOcrEngine"/>.
/// </para>
/// </summary>
public sealed class AzureFormRecognizerOcrEngine : IOcrEngine
{
    public string Vendor => "azure-document-intelligence";

    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _apiKey;
    private const string ApiVersion = "2024-11-30";
    private const string ModelId = "prebuilt-receipt";

    public AzureFormRecognizerOcrEngine(HttpClient http, string endpoint, string apiKey)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _endpoint = endpoint?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(endpoint));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
    }

    public async Task<OcrResult> RecogniseAsync(byte[] content, string contentType, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0)
        {
            return new OcrResult(null, null, null, 0);
        }

        // 1. Submit — Document Intelligence is asynchronous: POST returns a
        //    202 Accepted with an Operation-Location header to poll.
        var submitUrl = $"{_endpoint}/documentintelligence/documentModels/{ModelId}:analyze?api-version={ApiVersion}";
        using var submitReq = new HttpRequestMessage(HttpMethod.Post, submitUrl);
        submitReq.Headers.Add("Ocp-Apim-Subscription-Key", _apiKey);
        submitReq.Content = new ByteArrayContent(content);
        submitReq.Content.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);

        try
        {
            using var submitResp = await _http.SendAsync(submitReq, ct);
            if (!submitResp.IsSuccessStatusCode)
            {
                return new OcrResult(null, null, null, 0);
            }
            if (!submitResp.Headers.TryGetValues("Operation-Location", out var locs))
            {
                return new OcrResult(null, null, null, 0);
            }
            var pollUrl = locs.First();

            // 2. Poll up to ~25 seconds — receipts are small enough that
            //    DI typically returns within 2-5s.
            for (var i = 0; i < 25; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                using var pollReq = new HttpRequestMessage(HttpMethod.Get, pollUrl);
                pollReq.Headers.Add("Ocp-Apim-Subscription-Key", _apiKey);
                using var pollResp = await _http.SendAsync(pollReq, ct);
                if (!pollResp.IsSuccessStatusCode) continue;

                var json = await pollResp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var status = doc.RootElement.GetProperty("status").GetString();
                if (string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase))
                {
                    return Extract(doc);
                }
                if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    return new OcrResult(null, null, null, 0);
                }
            }
            return new OcrResult(null, null, null, 0); // timeout
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new OcrResult(null, null, null, 0);
        }
    }

    private static OcrResult Extract(JsonDocument doc)
    {
        // Document Intelligence returns analyzeResult.documents[0].fields.{Total,TransactionDate}
        // Each field has a "valueNumber" / "valueDate" + a "confidence".
        if (!doc.RootElement.TryGetProperty("analyzeResult", out var analyze)) return Empty();
        if (!analyze.TryGetProperty("documents", out var docs) || docs.GetArrayLength() == 0) return Empty();
        var d0 = docs[0];
        if (!d0.TryGetProperty("fields", out var fields)) return Empty();

        long? amountMinor = null;
        DateOnly? date = null;
        double avgConf = 0;
        var confCount = 0;

        if (fields.TryGetProperty("Total", out var total) && total.TryGetProperty("valueNumber", out var num))
        {
            // valueNumber is a JSON number representing the receipt total in major units.
            var major = num.GetDecimal();
            amountMinor = (long)Math.Round(major * 100m, 0, MidpointRounding.ToEven);
            if (total.TryGetProperty("confidence", out var c)) { avgConf += c.GetDouble(); confCount++; }
        }
        if (fields.TryGetProperty("TransactionDate", out var txDate) && txDate.TryGetProperty("valueDate", out var dStr))
        {
            if (DateOnly.TryParse(dStr.GetString(), out var d)) date = d;
            if (txDate.TryGetProperty("confidence", out var c)) { avgConf += c.GetDouble(); confCount++; }
        }

        var conf = confCount == 0 ? (byte)0 : (byte)Math.Round(avgConf / confCount * 100);
        var raw = analyze.TryGetProperty("content", out var content) ? content.GetString() : null;
        return new OcrResult(amountMinor, date, raw, conf);
    }

    private static OcrResult Empty() => new(null, null, null, 0);
}

/// <summary>
/// Composite engine that routes to the configured Azure engine when its
/// key is real, falls back to <see cref="NoopOcrEngine"/> otherwise.
/// Same shape as <c>RoutingEvatProvider</c> — same logic, different
/// interface.
/// </summary>
public sealed class RoutingOcrEngine : IOcrEngine
{
    private readonly IOcrEngine _real;
    private readonly IOcrEngine _noop;
    private readonly bool _useReal;

    public RoutingOcrEngine(IOcrEngine real, IOcrEngine noop, string? configuredKey)
    {
        _real = real;
        _noop = noop;
        _useReal = !string.IsNullOrWhiteSpace(configuredKey)
                   && !configuredKey.StartsWith("PLACEHOLDER", StringComparison.OrdinalIgnoreCase);
    }

    public string Vendor => _useReal ? _real.Vendor : _noop.Vendor;

    public Task<OcrResult> RecogniseAsync(byte[] content, string contentType, CancellationToken ct = default)
        => _useReal ? _real.RecogniseAsync(content, contentType, ct) : _noop.RecogniseAsync(content, contentType, ct);
}
