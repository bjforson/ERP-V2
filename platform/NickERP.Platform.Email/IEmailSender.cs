namespace NickERP.Platform.Email;

/// <summary>
/// Sprint 21 / Phase A — pluggable contract for sending outbound mail.
/// Implementations: <see cref="FileSystemEmailSender"/> (dev default;
/// writes <c>.eml</c> files to disk), <see cref="SmtpEmailSender"/>
/// (production; MailKit), <see cref="NoOpEmailSender"/> (tests +
/// disabled deployments).
/// </summary>
/// <remarks>
/// <para>
/// <b>Caller contract.</b> Throws <see cref="EmailSendException"/> on
/// permanent failures (auth rejected, connection refused after
/// retries, malformed recipient). Transient SMTP errors are retried
/// internally by <see cref="SmtpEmailSender"/>; if exhausted the
/// exception still surfaces to the caller. Cancellation throws
/// <see cref="OperationCanceledException"/>.
/// </para>
/// <para>
/// <b>Side-effect guarantee.</b> A successful return means the
/// message has been handed off to the underlying provider — for SMTP
/// that means accepted by the relay; for the filesystem provider
/// that means flushed to disk. No durable-queue semantics: a caller
/// that needs at-least-once delivery layers a queue on top.
/// </para>
/// </remarks>
public interface IEmailSender
{
    /// <summary>
    /// Send <paramref name="message"/>. Returns when the underlying
    /// provider has accepted the message. Throws
    /// <see cref="EmailSendException"/> on permanent errors.
    /// </summary>
    Task SendAsync(EmailMessage message, CancellationToken ct = default);

    /// <summary>
    /// Provider tag — "filesystem" / "smtp" / "noop". Useful for log
    /// lines and admin UIs that want to surface "what's wired".
    /// </summary>
    string ProviderName { get; }
}

/// <summary>
/// Permanent send failure. Wraps the underlying provider exception
/// when one exists; carries a defensive message + the recipient for
/// log lines.
/// </summary>
public sealed class EmailSendException : Exception
{
    /// <summary>The intended recipient. Captured for log lines.</summary>
    public string Recipient { get; }

    /// <summary>The provider tag (<see cref="IEmailSender.ProviderName"/>).</summary>
    public string Provider { get; }

    public EmailSendException(string provider, string recipient, string message, Exception? inner = null)
        : base(message, inner)
    {
        Provider = provider;
        Recipient = recipient;
    }
}
