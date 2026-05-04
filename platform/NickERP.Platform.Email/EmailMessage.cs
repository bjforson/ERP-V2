using System.Collections.ObjectModel;

namespace NickERP.Platform.Email;

/// <summary>
/// Sprint 21 / Phase A — provider-neutral outbound email payload.
/// Carried by <see cref="IEmailSender.SendAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Kept deliberately minimal. The first user is the invite-flow
/// (Sprint 21 / Phase B) which sends a transactional message with a
/// plaintext + HTML body and no attachments. Attachments / CC / BCC /
/// reply-to are explicit non-goals for v0; add them when a real caller
/// needs them.
/// </para>
/// <para>
/// <b>Defensive defaults.</b> Constructing without a non-empty
/// <see cref="To"/> + <see cref="Subject"/> + at least one body
/// throws — no partial-message accidents.
/// </para>
/// </remarks>
public sealed class EmailMessage
{
    /// <summary>
    /// Recipient email address. Single recipient by design — the v0
    /// callers (invite flow, password reset, etc.) all send 1:1
    /// messages. A future bulk-send path can layer on top.
    /// </summary>
    public string To { get; }

    /// <summary>
    /// Sender email. Set by the caller; senders may override to a
    /// no-reply default if blank — <see cref="SmtpEmailSender"/> reads
    /// <c>Email:DefaultFrom</c> as a fallback.
    /// </summary>
    public string? From { get; }

    /// <summary>Subject line. Required.</summary>
    public string Subject { get; }

    /// <summary>
    /// Plaintext body. At least one of <see cref="BodyText"/> or
    /// <see cref="BodyHtml"/> must be non-empty. Senders that emit
    /// multipart/alternative include both when both are present;
    /// otherwise a single body is sent.
    /// </summary>
    public string? BodyText { get; }

    /// <summary>HTML body. See <see cref="BodyText"/> for the at-least-one rule.</summary>
    public string? BodyHtml { get; }

    /// <summary>
    /// Optional headers (e.g. <c>X-NickERP-Template</c>). Passed through
    /// by senders that support it (MailKit does); the filesystem sender
    /// writes them into the .eml header block.
    /// </summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>
    /// Optional display-name overrides. <see cref="ToDisplayName"/> is
    /// used in the <c>To:</c> header (e.g. "Jane Doe" &lt;jane@example.com&gt;);
    /// <see cref="FromDisplayName"/> in the <c>From:</c> header.
    /// </summary>
    public string? ToDisplayName { get; }

    /// <summary>See <see cref="ToDisplayName"/>.</summary>
    public string? FromDisplayName { get; }

    public EmailMessage(
        string to,
        string subject,
        string? bodyText = null,
        string? bodyHtml = null,
        string? from = null,
        IReadOnlyDictionary<string, string>? headers = null,
        string? toDisplayName = null,
        string? fromDisplayName = null)
    {
        if (string.IsNullOrWhiteSpace(to))
            throw new ArgumentException("Recipient email is required.", nameof(to));
        if (string.IsNullOrWhiteSpace(subject))
            throw new ArgumentException("Subject is required.", nameof(subject));
        if (string.IsNullOrWhiteSpace(bodyText) && string.IsNullOrWhiteSpace(bodyHtml))
            throw new ArgumentException("At least one of bodyText or bodyHtml must be non-empty.", nameof(bodyText));

        To = to.Trim();
        Subject = subject;
        BodyText = bodyText;
        BodyHtml = bodyHtml;
        From = string.IsNullOrWhiteSpace(from) ? null : from.Trim();
        ToDisplayName = string.IsNullOrWhiteSpace(toDisplayName) ? null : toDisplayName;
        FromDisplayName = string.IsNullOrWhiteSpace(fromDisplayName) ? null : fromDisplayName;
        Headers = headers is null
            ? ReadOnlyDictionary<string, string>.Empty
            : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase));
    }
}
