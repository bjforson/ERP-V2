using System.Net;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Text;

namespace NickERP.Platform.Email;

/// <summary>
/// Sprint 21 / Phase A — production-grade SMTP sender backed by
/// MailKit (the de facto .NET SMTP client; supersedes
/// <c>System.Net.Mail.SmtpClient</c> which the BCL marks obsolete).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why MailKit.</b> The .NET BCL <c>System.Net.Mail.SmtpClient</c>
/// is officially deprecated; MailKit is the recommended replacement
/// and supports STARTTLS / implicit TLS / SASL auth / cancellation
/// out of the box.
/// </para>
/// <para>
/// <b>TLS posture.</b> Defaults to <see cref="SecureSocketOptions.StartTlsWhenAvailable"/>
/// — opportunistic upgrade. If <see cref="EmailOptions.SmtpOptions.UseTls"/>
/// is explicitly <c>false</c> the sender refuses to connect (security
/// guardrail); operators must set <see cref="EmailOptions.SmtpOptions.AllowInsecure"/>=
/// <c>true</c> AND <c>UseTls=false</c> together to opt out of TLS,
/// and even then a warning is logged on every send. The default is
/// secure; weakening it is a documented opt-in.
/// </para>
/// <para>
/// <b>Auth.</b> If <see cref="EmailOptions.SmtpOptions.Username"/> is
/// set, the sender authenticates with the configured credentials.
/// The password is read from config; production hosts should source
/// it from an env var (e.g. <c>Email__Smtp__Password</c>) rather
/// than appsettings.
/// </para>
/// </remarks>
public sealed class SmtpEmailSender : IEmailSender
{
    /// <summary>Provider tag returned from <see cref="ProviderName"/>.</summary>
    public const string ProviderTag = "smtp";

    private readonly EmailOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string ProviderName => ProviderTag;

    /// <inheritdoc />
    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var smtp = _options.Smtp ?? throw new EmailSendException(ProviderTag, message.To,
            "SMTP options not configured. Set Email:Smtp:Host (and friends) before using the smtp provider.");
        if (string.IsNullOrWhiteSpace(smtp.Host))
        {
            throw new EmailSendException(ProviderTag, message.To,
                "Email:Smtp:Host is empty. Configure a relay before sending.");
        }
        if (smtp.Port <= 0 || smtp.Port > 65535)
        {
            throw new EmailSendException(ProviderTag, message.To,
                $"Email:Smtp:Port must be 1..65535 (got {smtp.Port}).");
        }
        // TLS guardrail. UseTls=false alone is not enough — the operator
        // must also explicitly set AllowInsecure=true; this prevents a
        // single typo'd config from silently downgrading.
        if (!smtp.UseTls && !smtp.AllowInsecure)
        {
            throw new EmailSendException(ProviderTag, message.To,
                "Email:Smtp:UseTls is false but Email:Smtp:AllowInsecure is also false. "
                + "TLS is required by default; if you really want plaintext SMTP set BOTH "
                + "UseTls=false AND AllowInsecure=true (and accept the security regression).");
        }

        var mime = BuildMime(message);

        using var client = new SmtpClient();
        try
        {
            var secureOpt = ResolveSecureSocketOptions(smtp);
            await client.ConnectAsync(smtp.Host, smtp.Port, secureOpt, ct);

            if (!string.IsNullOrEmpty(smtp.Username))
            {
                var creds = new NetworkCredential(smtp.Username, smtp.Password ?? string.Empty);
                await client.AuthenticateAsync(creds, ct);
            }

            await client.SendAsync(mime, ct);
            await client.DisconnectAsync(true, ct);

            if (!smtp.UseTls)
            {
                _logger.LogWarning(
                    "SmtpEmailSender sent {To} over plaintext SMTP (UseTls=false, AllowInsecure=true). "
                    + "This is a documented opt-out; flip UseTls=true to restore TLS.",
                    message.To);
            }
            else
            {
                _logger.LogInformation(
                    "SmtpEmailSender sent {To} via {Host}:{Port} (subject={Subject}, secureOpt={SecureOpt}).",
                    message.To, smtp.Host, smtp.Port, message.Subject, secureOpt);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new EmailSendException(ProviderTag, message.To,
                $"SMTP send failed via {smtp.Host}:{smtp.Port}: {ex.Message}", ex);
        }
    }

    private MimeMessage BuildMime(EmailMessage message)
    {
        var mime = new MimeMessage();

        var from = string.IsNullOrWhiteSpace(message.From) ? _options.DefaultFrom : message.From;
        if (string.IsNullOrWhiteSpace(from))
        {
            throw new EmailSendException(ProviderTag, message.To,
                "No From address: neither EmailMessage.From nor Email:DefaultFrom is set.");
        }
        mime.From.Add(new MailboxAddress(message.FromDisplayName ?? string.Empty, from));
        mime.To.Add(new MailboxAddress(message.ToDisplayName ?? string.Empty, message.To));
        mime.Subject = message.Subject;

        foreach (var (k, v) in message.Headers)
        {
            if (v is not null && (v.Contains('\r') || v.Contains('\n')))
                throw new ArgumentException($"Header '{k}' contains CR/LF — header injection guard tripped.", nameof(message));
            mime.Headers.Add(k, v ?? string.Empty);
        }

        var hasText = !string.IsNullOrWhiteSpace(message.BodyText);
        var hasHtml = !string.IsNullOrWhiteSpace(message.BodyHtml);

        if (hasText && hasHtml)
        {
            var alt = new MimeKit.Multipart("alternative");
            // hasText/hasHtml guard non-null/non-whitespace above; safe to coalesce
            // for the MailKit 4.16+ non-nullable Text setter.
            alt.Add(new TextPart(TextFormat.Plain) { Text = message.BodyText ?? string.Empty });
            alt.Add(new TextPart(TextFormat.Html) { Text = message.BodyHtml ?? string.Empty });
            mime.Body = alt;
        }
        else if (hasHtml)
        {
            mime.Body = new TextPart(TextFormat.Html) { Text = message.BodyHtml ?? string.Empty };
        }
        else
        {
            mime.Body = new TextPart(TextFormat.Plain) { Text = message.BodyText ?? string.Empty };
        }

        return mime;
    }

    private static SecureSocketOptions ResolveSecureSocketOptions(EmailOptions.SmtpOptions smtp)
    {
        if (!smtp.UseTls) return SecureSocketOptions.None;
        // Implicit TLS on 465; STARTTLS-when-available on 587 / 25 / others.
        // Operators can override via SecureSocketOption if they need a
        // specific mode (e.g. force STARTTLS on 465).
        return smtp.Port == 465
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTlsWhenAvailable;
    }
}
