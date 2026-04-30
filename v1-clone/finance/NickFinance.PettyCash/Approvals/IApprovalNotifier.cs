using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace NickFinance.PettyCash.Approvals;

/// <summary>
/// Side-channel for surfacing a pending approval to the approver. The
/// WebApp's in-UI approval queue is the primary path; this is the
/// out-of-band fallback for "wake the CFO at 8pm" cases — large vouchers,
/// AP payment runs, anything tagged urgent in policy. Hooked into the
/// approval engine so engines fire-and-forget; failures here do not block
/// the on-screen approval.
/// </summary>
public interface IApprovalNotifier
{
    /// <summary>Channel tag persisted on the audit row (<c>"whatsapp"</c>, <c>"sms"</c>, …).</summary>
    string Channel { get; }

    /// <summary>Send the notification. Implementations should swallow transient failures and surface only on the audit row.</summary>
    Task<NotifierResult> NotifyAsync(ApprovalNotification msg, CancellationToken ct = default);
}

public sealed record ApprovalNotification(
    Guid VoucherId,
    string VoucherNo,
    long AmountMinor,
    string CurrencyCode,
    string ApproverIdentifier,        // phone / email / team id — channel-specific
    string Purpose,
    long TenantId = 1);

public sealed record NotifierResult(
    bool Delivered,
    string Channel,
    string? VendorMessageId,
    string? FailureReason);

/// <summary>
/// No-op notifier. The default — keeps the approval engine hookable in
/// host configurations that don't have an out-of-band channel wired yet.
/// </summary>
public sealed class NoopApprovalNotifier : IApprovalNotifier
{
    public string Channel => "noop";
    public Task<NotifierResult> NotifyAsync(ApprovalNotification msg, CancellationToken ct = default)
        => Task.FromResult(new NotifierResult(false, Channel, null, "no notifier configured"));
}

/// <summary>
/// WhatsApp Cloud API approval notifier. Sends a templated message via
/// Meta's WhatsApp Business Cloud API (or via NickComms.Gateway's
/// <c>/api/whatsapp/template-message</c> endpoint when that ships — which
/// keeps the Meta credentials out of every consuming app). For now this
/// implementation talks directly to Meta — flip <c>NickFinance:WhatsApp:Mode</c>
/// to <c>"gateway"</c> later.
///
/// <para>
/// Configuration:
/// <list type="bullet">
///   <item><c>WHATSAPP_CLOUD_API_TOKEN</c> — bearer for graph.facebook.com.</item>
///   <item><c>WHATSAPP_PHONE_NUMBER_ID</c> — sender phone number id from Meta.</item>
///   <item><c>NickFinance:WhatsApp:TemplateName</c> — defaults to <c>nickerp_voucher_approval</c>.</item>
///   <item><c>NickFinance:WhatsApp:LanguageCode</c> — defaults to <c>en</c>.</item>
/// </list>
/// </para>
/// </summary>
public sealed class WhatsAppApprovalNotifier : IApprovalNotifier
{
    public string Channel => "whatsapp";

    private readonly HttpClient _http;
    private readonly string _phoneNumberId;
    private readonly string _templateName;
    private readonly string _languageCode;

    public WhatsAppApprovalNotifier(HttpClient http, string phoneNumberId, string? templateName = null, string? languageCode = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _phoneNumberId = phoneNumberId ?? throw new ArgumentNullException(nameof(phoneNumberId));
        _templateName = string.IsNullOrWhiteSpace(templateName) ? "nickerp_voucher_approval" : templateName;
        _languageCode = string.IsNullOrWhiteSpace(languageCode) ? "en" : languageCode;
    }

    public async Task<NotifierResult> NotifyAsync(ApprovalNotification msg, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(msg);
        if (string.IsNullOrWhiteSpace(msg.ApproverIdentifier))
        {
            return new NotifierResult(false, Channel, null, "approver phone is required");
        }

        // Meta template body — the approver's phone is in E.164 form.
        var body = new
        {
            messaging_product = "whatsapp",
            to = msg.ApproverIdentifier,
            type = "template",
            template = new
            {
                name = _templateName,
                language = new { code = _languageCode },
                components = new[]
                {
                    new
                    {
                        type = "body",
                        parameters = new object[]
                        {
                            new { type = "text", text = msg.VoucherNo },
                            new { type = "text", text = $"{msg.AmountMinor / 100m:N2} {msg.CurrencyCode}" },
                            new { type = "text", text = Truncate(msg.Purpose, 80) }
                        }
                    }
                }
            }
        };

        try
        {
            using var resp = await _http.PostAsJsonAsync($"/v20.0/{_phoneNumberId}/messages", body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var raw = await resp.Content.ReadAsStringAsync(ct);
                return new NotifierResult(false, Channel, null, $"WhatsApp HTTP {(int)resp.StatusCode}: {Truncate(raw, 240)}");
            }
            var ack = await resp.Content.ReadFromJsonAsync<WaAck>(cancellationToken: ct);
            var id = ack?.Messages?.FirstOrDefault()?.Id;
            return new NotifierResult(true, Channel, id, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new NotifierResult(false, Channel, null, $"WhatsApp unreachable: {ex.Message}");
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? string.Empty : s.Length <= max ? s : s[..max] + "…";

    private sealed record WaAck(
        [property: JsonPropertyName("messages")] WaMessage[]? Messages);
    private sealed record WaMessage(
        [property: JsonPropertyName("id")] string? Id);
}

/// <summary>
/// Composite notifier — same routing trick as
/// <c>RoutingEvatProvider</c> / <c>RoutingOcrEngine</c>: real implementation
/// when its token is real, no-op otherwise.
/// </summary>
public sealed class RoutingApprovalNotifier : IApprovalNotifier
{
    private readonly IApprovalNotifier _real;
    private readonly IApprovalNotifier _noop;
    private readonly bool _useReal;

    public RoutingApprovalNotifier(IApprovalNotifier real, IApprovalNotifier noop, string? configuredToken)
    {
        _real = real;
        _noop = noop;
        _useReal = !string.IsNullOrWhiteSpace(configuredToken)
                   && !configuredToken.StartsWith("PLACEHOLDER", StringComparison.OrdinalIgnoreCase);
    }

    public string Channel => _useReal ? _real.Channel : _noop.Channel;

    public Task<NotifierResult> NotifyAsync(ApprovalNotification msg, CancellationToken ct = default)
        => _useReal ? _real.NotifyAsync(msg, ct) : _noop.NotifyAsync(msg, ct);
}
