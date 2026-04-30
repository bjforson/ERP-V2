using System.Net.Http.Json;

namespace NickFinance.PettyCash.Disbursement;

/// <summary>
/// MoMo channel implemented as an HTTP call to NickComms.Gateway's
/// <c>/api/disburse/momo</c> endpoint, which in turn fronts Hubtel
/// Merchant API. Petty Cash never talks to Hubtel directly — that
/// keeps the merchant credentials in one place (NickComms) and lets
/// us add SMS / WhatsApp confirmations on the same path.
/// </summary>
/// <remarks>
/// <para>
/// Configuration (from the host's <c>appsettings.json</c>):
/// </para>
/// <code>
/// "NickFinance:Momo": {
///   "GatewayBaseUrl": "https://comms.nickerp.local",
///   "ApiKey": "&lt;NICKCOMMS_API_KEY_NICKFINANCE&gt;"
/// }
/// </code>
/// <para>
/// Wired with a typed <see cref="HttpClient"/>; the host configures the
/// base URL + the <c>X-Api-Key</c> header. This class is therefore
/// transport-agnostic in tests — substitute a fake
/// <see cref="HttpMessageHandler"/> for unit testing without booting
/// NickComms.
/// </para>
/// <para>
/// Errors map to <see cref="DisbursementResult.Accepted"/> = false with
/// <see cref="DisbursementResult.FailureReason"/> populated. Petty Cash's
/// service treats a non-accepted disbursement as a soft failure: the
/// journal is NOT posted, the voucher stays Approved, the operator gets
/// a chance to retry once Hubtel comes back.
/// </para>
/// </remarks>
public sealed class NickCommsMomoChannel : IDisbursementChannel
{
    public string Channel => "momo:hubtel";

    private readonly HttpClient _http;

    public NickCommsMomoChannel(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<DisbursementResult> DisburseAsync(DisbursementRequest req, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (string.IsNullOrWhiteSpace(req.PayeeMomoNumber))
        {
            return new DisbursementResult(false, Channel, null, "Payee MoMo number is required.");
        }
        if (string.IsNullOrWhiteSpace(req.PayeeMomoNetwork))
        {
            return new DisbursementResult(false, Channel, null, "Payee MoMo network is required (MTN / VODA / ATM).");
        }

        var payload = new
        {
            clientReference = req.ClientReference,
            amountMinor = req.AmountMinor,
            currency = req.CurrencyCode,
            payeeName = req.PayeeName,
            momoNumber = req.PayeeMomoNumber,
            momoNetwork = req.PayeeMomoNetwork,
            voucherNo = req.VoucherNo,
            tenantId = req.TenantId
        };

        try
        {
            var resp = await _http.PostAsJsonAsync("/api/disburse/momo", payload, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                return new DisbursementResult(false, Channel, null,
                    $"Gateway returned HTTP {(int)resp.StatusCode}: {Truncate(body, 240)}");
            }

            // The gateway returns { transactionId, status }
            var ack = await resp.Content.ReadFromJsonAsync<MomoAck>(cancellationToken: ct)
                ?? new MomoAck("(missing transaction id)", "unknown");
            return new DisbursementResult(true, Channel, ack.TransactionId, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new DisbursementResult(false, Channel, null, $"Gateway unreachable: {ex.Message}");
        }
    }

    private sealed record MomoAck(string TransactionId, string Status);

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? string.Empty :
        s.Length <= max ? s : s[..max] + "…";
}
