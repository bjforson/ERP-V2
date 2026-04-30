namespace NickFinance.WebApp.Services;

/// <summary>
/// Outbound email transport. Implementations may be SMTP-backed
/// (<see cref="SmtpEmailService"/>) or no-op (<see cref="NoopEmailService"/>)
/// when env vars aren't set. The interface returns a bool so callers can
/// surface "configured / not configured" without surfacing a stack trace.
/// </summary>
public interface IEmailService
{
    /// <summary>Send the given message. Returns <c>true</c> on success,
    /// <c>false</c> if the implementation is the no-op fallback or if the
    /// underlying transport refused. Exceptions bubble for caller-handled
    /// configuration errors only — transient SMTP failures are caught
    /// and logged inside the implementation.</summary>
    Task<bool> SendAsync(EmailMessage msg, CancellationToken ct = default);
}

/// <summary>One outbound email.</summary>
/// <param name="To">Recipient address.</param>
/// <param name="Subject">Subject line.</param>
/// <param name="BodyHtml">HTML body (will be rendered as multipart/alternative).</param>
/// <param name="BodyText">Optional plain-text alternative; auto-derived from <paramref name="BodyHtml"/> when null (rough strip).</param>
/// <param name="Attachments">File attachments. Null/empty = no attachments.</param>
public sealed record EmailMessage(
    string To,
    string Subject,
    string BodyHtml,
    string? BodyText = null,
    IReadOnlyList<EmailAttachment>? Attachments = null);

/// <summary>One file attached to an outbound email.</summary>
/// <param name="Filename">e.g. "statement.pdf".</param>
/// <param name="ContentType">MIME type, e.g. "application/pdf".</param>
/// <param name="Content">Raw bytes.</param>
public sealed record EmailAttachment(
    string Filename,
    string ContentType,
    byte[] Content);
