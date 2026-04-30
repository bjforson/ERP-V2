using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace NickFinance.WebApp.Services;

/// <summary>
/// Configuration for <see cref="SmtpEmailService"/>. Env-var-gated:
/// missing host or username collapses to the no-op service via
/// <see cref="EmailServiceFactory"/>.
/// </summary>
public sealed record SmtpOptions(
    string Host,
    int Port,
    string Username,
    string Password,
    string FromAddress,
    string FromDisplayName,
    bool UseStartTls);

/// <summary>
/// Real MailKit-backed SMTP sender. Uses STARTTLS by default (post-587)
/// — implicit TLS only kicks in when <see cref="SmtpOptions.UseStartTls"/>
/// is false AND the port is 465. Connection / send failures are caught
/// and logged; the method returns false rather than bubbling so a
/// transient outage doesn't take down the request.
/// </summary>
public sealed class SmtpEmailService : IEmailService
{
    private readonly SmtpOptions _opts;
    private readonly ILogger<SmtpEmailService> _log;

    public SmtpEmailService(SmtpOptions opts, ILogger<SmtpEmailService> log)
    {
        _opts = opts ?? throw new ArgumentNullException(nameof(opts));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <inheritdoc/>
    public async Task<bool> SendAsync(EmailMessage msg, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(msg);

        var mime = BuildMime(msg);
        try
        {
            using var client = new SmtpClient();
            var secure = _opts.UseStartTls
                ? SecureSocketOptions.StartTls
                : (_opts.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.Auto);
            await client.ConnectAsync(_opts.Host, _opts.Port, secure, ct);
            if (!string.IsNullOrEmpty(_opts.Username))
            {
                await client.AuthenticateAsync(_opts.Username, _opts.Password, ct);
            }
            await client.SendAsync(mime, ct);
            await client.DisconnectAsync(quit: true, ct);
            _log.LogInformation("SMTP send OK to {To} subject={Subject}", msg.To, msg.Subject);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "SMTP send failed to {To} subject={Subject}", msg.To, msg.Subject);
            return false;
        }
    }

    /// <summary>Build a MimeMessage from our DTO. Exposed internally so
    /// tests can verify the on-wire shape without touching a network.</summary>
    internal MimeMessage BuildMime(EmailMessage msg)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(_opts.FromDisplayName, _opts.FromAddress));
        mime.To.Add(MailboxAddress.Parse(msg.To));
        mime.Subject = msg.Subject;

        var body = new BodyBuilder
        {
            HtmlBody = msg.BodyHtml,
            TextBody = msg.BodyText ?? StripTags(msg.BodyHtml),
        };
        if (msg.Attachments is { Count: > 0 } atts)
        {
            foreach (var a in atts)
            {
                body.Attachments.Add(a.Filename, a.Content, ContentType.Parse(a.ContentType));
            }
        }
        mime.Body = body.ToMessageBody();
        return mime;
    }

    private static string StripTags(string html) =>
        System.Text.RegularExpressions.Regex.Replace(html ?? string.Empty, "<[^>]+>", string.Empty);
}

/// <summary>
/// Fallback when SMTP isn't configured. Logs the attempt and returns
/// false so the UI can show "not configured" rather than silently failing.
/// </summary>
public sealed class NoopEmailService : IEmailService
{
    private readonly ILogger<NoopEmailService> _log;
    public NoopEmailService(ILogger<NoopEmailService> log) => _log = log;

    public Task<bool> SendAsync(EmailMessage msg, CancellationToken ct = default)
    {
        _log.LogWarning("Email send skipped (NICKFINANCE_SMTP_* not configured): To={To} Subject={Subject} Attachments={Att}",
            msg.To, msg.Subject, msg.Attachments?.Count ?? 0);
        return Task.FromResult(false);
    }
}

/// <summary>
/// Static factory that resolves which <see cref="IEmailService"/> to
/// register based on env-var presence. Mirrors the routing pattern the
/// other adapters (e-VAT, OCR, MoMo, WhatsApp) already use.
/// </summary>
public static class EmailServiceFactory
{
    /// <summary>Read env vars; return a populated <see cref="SmtpOptions"/>
    /// or null when host/username are missing (which means: register the
    /// no-op).</summary>
    public static SmtpOptions? ReadOptionsFromEnv()
    {
        var host = Environment.GetEnvironmentVariable("NICKFINANCE_SMTP_HOST");
        var portStr = Environment.GetEnvironmentVariable("NICKFINANCE_SMTP_PORT");
        var user = Environment.GetEnvironmentVariable("NICKFINANCE_SMTP_USERNAME");
        var pass = Environment.GetEnvironmentVariable("NICKFINANCE_SMTP_PASSWORD") ?? string.Empty;
        var from = Environment.GetEnvironmentVariable("NICKFINANCE_SMTP_FROM");
        var startTlsStr = Environment.GetEnvironmentVariable("NICKFINANCE_SMTP_USE_STARTTLS");

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(from))
        {
            return null;
        }

        var port = int.TryParse(portStr, out var p) && p > 0 ? p : 587;
        var useStartTls = !string.Equals(startTlsStr, "false", StringComparison.OrdinalIgnoreCase);
        var (display, addr) = ParseFrom(from);
        return new SmtpOptions(host, port, user, pass, addr, display, useStartTls);
    }

    /// <summary>Split "Display Name &lt;addr@example.com&gt;" into its parts.
    /// Falls back to using the whole string as the address when there's
    /// no display-name section.</summary>
    internal static (string Display, string Address) ParseFrom(string from)
    {
        var s = from.Trim();
        var lt = s.LastIndexOf('<');
        var gt = s.LastIndexOf('>');
        if (lt > 0 && gt > lt)
        {
            var display = s[..lt].Trim();
            var addr = s[(lt + 1)..gt].Trim();
            return (display, addr);
        }
        return (PdfBrandingFallbackName, s);
    }

    private const string PdfBrandingFallbackName = "Nick TC-Scan Ltd";
}
