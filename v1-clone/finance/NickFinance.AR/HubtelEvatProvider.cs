using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace NickFinance.AR;

/// <summary>
/// Hubtel-fronted GRA e-VAT provider. Hubtel is one of three GRA-certified
/// e-VAT partners (alongside Persol Systems and Blue Skies Solutions); this
/// implementation is concrete enough to swap in once the merchant
/// onboarding completes and a sandbox API key is issued.
///
/// <para>
/// Configuration (machine env vars or appsettings):
/// <list type="bullet">
///   <item><c>NickFinance:Evat:BaseUrl</c> — the Hubtel sandbox/prod URL.</item>
///   <item><c>NICKFINANCE_EVAT_API_KEY</c> — the merchant key (machine env var).</item>
/// </list>
/// </para>
///
/// <para>
/// Until the live key arrives, the host can register this provider with a
/// placeholder key. Every IRN it tries to issue against an unreachable
/// endpoint comes back as <c>Accepted=false</c> with a connect failure;
/// AR's invoice flow falls through to <see cref="StubEvatProvider"/>
/// behaviour via the <c>IEvatProviderRouter</c> seam.
/// </para>
///
/// <para>
/// IMPORTANT: when activating in production, set
/// <c>NICKFINANCE_EVAT_API_KEY</c> to a real value and verify a sandbox
/// round-trip first (see <c>finance/DEFERRED.md</c> §1 acceptance steps).
/// </para>
/// </summary>
public sealed class HubtelEvatProvider : IEvatProvider
{
    public string Provider => "hubtel";

    private readonly HttpClient _http;

    public HubtelEvatProvider(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<EvatIssueResult> IssueAsync(EvatIssueRequest req, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (req.GrossMinor <= 0)
        {
            return new EvatIssueResult(false, null, null, "Gross amount must be positive.");
        }

        // Hubtel's eVAT body shape — fields documented in the partner
        // onboarding pack. Amounts go in major-units (decimal cedis), not
        // minor, because that's what Hubtel's REST API takes.
        var body = new HubtelInvoiceRequest(
            ClientReference: req.InvoiceId.ToString("N"),
            InvoiceNo: req.InvoiceNo,
            CustomerName: req.CustomerName,
            CustomerTin: req.CustomerTin ?? string.Empty,
            InvoiceDate: req.InvoiceDate.ToString("yyyy-MM-dd"),
            Currency: req.CurrencyCode,
            NetAmount: ToMajor(req.NetMinor),
            LeviesAmount: ToMajor(req.LeviesMinor),
            VatAmount: ToMajor(req.VatMinor),
            GrossAmount: ToMajor(req.GrossMinor));

        try
        {
            using var resp = await _http.PostAsJsonAsync("/api/invoices", body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var raw = await resp.Content.ReadAsStringAsync(ct);
                return new EvatIssueResult(false, null, null,
                    $"Hubtel returned HTTP {(int)resp.StatusCode}: {Truncate(raw, 240)}");
            }
            var ack = await resp.Content.ReadFromJsonAsync<HubtelInvoiceAck>(cancellationToken: ct);
            if (ack is null || string.IsNullOrWhiteSpace(ack.Irn))
            {
                return new EvatIssueResult(false, null, null, "Hubtel ack missing IRN.");
            }
            return new EvatIssueResult(true, ack.Irn, ack.QrPayload, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new EvatIssueResult(false, null, null, $"Hubtel unreachable: {ex.Message}");
        }
    }

    private static decimal ToMajor(long minor) => minor / 100m;

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? string.Empty : s.Length <= max ? s : s[..max] + "…";

    private sealed record HubtelInvoiceRequest(
        [property: JsonPropertyName("clientReference")] string ClientReference,
        [property: JsonPropertyName("invoiceNo")] string InvoiceNo,
        [property: JsonPropertyName("customerName")] string CustomerName,
        [property: JsonPropertyName("customerTin")] string CustomerTin,
        [property: JsonPropertyName("invoiceDate")] string InvoiceDate,
        [property: JsonPropertyName("currency")] string Currency,
        [property: JsonPropertyName("netAmount")] decimal NetAmount,
        [property: JsonPropertyName("leviesAmount")] decimal LeviesAmount,
        [property: JsonPropertyName("vatAmount")] decimal VatAmount,
        [property: JsonPropertyName("grossAmount")] decimal GrossAmount);

    private sealed record HubtelInvoiceAck(
        [property: JsonPropertyName("irn")] string? Irn,
        [property: JsonPropertyName("qrPayload")] string? QrPayload);
}

/// <summary>
/// Composite provider that routes to the configured production
/// <see cref="IEvatProvider"/> when its key is set to a real value, and
/// falls back to <see cref="StubEvatProvider"/> when the key looks like a
/// placeholder (literal <c>"PLACEHOLDER..."</c> or empty). The fallback
/// keeps the system shippable before the real key lands.
/// </summary>
public sealed class RoutingEvatProvider : IEvatProvider
{
    private readonly IEvatProvider _real;
    private readonly IEvatProvider _stub;
    private readonly bool _useReal;

    public RoutingEvatProvider(IEvatProvider real, IEvatProvider stub, string? configuredKey)
    {
        _real = real;
        _stub = stub;
        _useReal = !string.IsNullOrWhiteSpace(configuredKey)
                   && !configuredKey.StartsWith("PLACEHOLDER", StringComparison.OrdinalIgnoreCase);
    }

    public string Provider => _useReal ? _real.Provider : _stub.Provider;

    public Task<EvatIssueResult> IssueAsync(EvatIssueRequest req, CancellationToken ct = default)
        => _useReal ? _real.IssueAsync(req, ct) : _stub.IssueAsync(req, ct);
}
