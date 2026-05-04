using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NickERP.Platform.Email;

/// <summary>
/// Sprint 21 / Phase A — dev/test sender that writes outgoing mail
/// to disk as RFC-822-ish <c>.eml</c> files. Default provider for the
/// <c>Development</c> environment.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a filesystem sender.</b> The Sprint 21 invite flow needs an
/// end-to-end exercisable path on the dev box: tenant create -&gt;
/// invite issue -&gt; .eml lands -&gt; operator opens it -&gt;
/// clicks the link -&gt; sets a password. Requiring an SMTP relay
/// for that loop slows iteration; this sender drops the message
/// where the operator can read it (<c>var/email-outbox/</c> by
/// default).
/// </para>
/// <para>
/// <b>Format.</b> The file is a minimal RFC 822 envelope: <c>To</c> /
/// <c>From</c> / <c>Subject</c> / <c>Date</c> + custom headers, blank
/// line, then either a single body or a <c>multipart/alternative</c>
/// boundary when both text + html are present. It opens in any
/// modern MUA (Outlook, Thunderbird, Apple Mail) for visual review.
/// </para>
/// <para>
/// <b>Filename.</b> <c>yyyyMMdd-HHmmssfff_{slug-of-recipient}_{slug-of-subject}.eml</c>.
/// Subject + recipient are slugified to keep the filename portable
/// and readable when triaging dozens of messages.
/// </para>
/// </remarks>
public sealed class FileSystemEmailSender : IEmailSender
{
    /// <summary>Provider tag returned from <see cref="ProviderName"/>.</summary>
    public const string ProviderTag = "filesystem";

    private readonly EmailOptions _options;
    private readonly ILogger<FileSystemEmailSender> _logger;
    private readonly TimeProvider _clock;

    public FileSystemEmailSender(
        IOptions<EmailOptions> options,
        ILogger<FileSystemEmailSender> logger,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string ProviderName => ProviderTag;

    /// <inheritdoc />
    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var dir = ResolveOutboxDir();
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            throw new EmailSendException(ProviderTag, message.To,
                $"Failed to create email outbox directory '{dir}': {ex.Message}", ex);
        }

        var filename = BuildFilename(message);
        var fullPath = Path.Combine(dir, filename);
        var contents = BuildEmlContents(message);

        try
        {
            await File.WriteAllTextAsync(fullPath, contents, Encoding.UTF8, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new EmailSendException(ProviderTag, message.To,
                $"Failed to write .eml to '{fullPath}': {ex.Message}", ex);
        }

        _logger.LogInformation(
            "FileSystemEmailSender wrote {Path} (to={To}, subject={Subject}).",
            fullPath, message.To, message.Subject);
    }

    private string ResolveOutboxDir()
    {
        // Allow absolute or relative configuration; relative paths
        // resolve against the host's current directory (typically the
        // ContentRoot when running via dotnet / Windows service host).
        var configured = string.IsNullOrWhiteSpace(_options.FileSystem?.OutboxDirectory)
            ? Path.Combine("var", "email-outbox")
            : _options.FileSystem!.OutboxDirectory!;
        return Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(Directory.GetCurrentDirectory(), configured);
    }

    private string BuildFilename(EmailMessage message)
    {
        var ts = _clock.GetUtcNow().ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        var rcpt = Slugify(message.To);
        var subj = Slugify(message.Subject);
        // Keep the filename under typical filesystem limits (~255).
        if (subj.Length > 60) subj = subj[..60];
        return $"{ts}_{rcpt}_{subj}.eml";
    }

    private static string Slugify(string s)
    {
        if (string.IsNullOrEmpty(s)) return "x";
        // Replace anything non-alphanumeric with '-', collapse runs,
        // trim trailing '-'. Matches the convention v1's BackupOrchestrator
        // uses for filename slugification (deliberate: ops who triage
        // both v1 and v2 see the same shape).
        var lower = s.ToLowerInvariant();
        var subbed = Regex.Replace(lower, "[^a-z0-9]+", "-");
        return subbed.Trim('-');
    }

    private string BuildEmlContents(EmailMessage message)
    {
        var sb = new StringBuilder();
        var from = string.IsNullOrWhiteSpace(message.From) ? _options.DefaultFrom : message.From;
        if (string.IsNullOrWhiteSpace(from)) from = "no-reply@nickerp.local";

        sb.Append("From: ").Append(FormatAddress(from!, message.FromDisplayName)).Append("\r\n");
        sb.Append("To: ").Append(FormatAddress(message.To, message.ToDisplayName)).Append("\r\n");
        sb.Append("Subject: ").Append(message.Subject).Append("\r\n");
        sb.Append("Date: ").Append(_clock.GetUtcNow().ToString("R", CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("MIME-Version: 1.0\r\n");

        foreach (var (k, v) in message.Headers)
        {
            // Disallow newlines in custom header values to avoid header injection if a caller
            // ever passes user-controlled data through unchecked. Belt-and-braces; the invite
            // flow only uses constants, but the abstraction is general.
            if (v is not null && (v.Contains('\r') || v.Contains('\n')))
                throw new ArgumentException($"Header '{k}' contains CR/LF — header injection guard tripped.", nameof(message));
            sb.Append(k).Append(": ").Append(v).Append("\r\n");
        }

        var hasText = !string.IsNullOrWhiteSpace(message.BodyText);
        var hasHtml = !string.IsNullOrWhiteSpace(message.BodyHtml);

        if (hasText && hasHtml)
        {
            var boundary = "----nickerp-eml-" + Guid.NewGuid().ToString("N");
            sb.Append("Content-Type: multipart/alternative; boundary=\"").Append(boundary).Append("\"\r\n\r\n");
            sb.Append("--").Append(boundary).Append("\r\n");
            sb.Append("Content-Type: text/plain; charset=utf-8\r\n\r\n");
            sb.Append(message.BodyText).Append("\r\n");
            sb.Append("--").Append(boundary).Append("\r\n");
            sb.Append("Content-Type: text/html; charset=utf-8\r\n\r\n");
            sb.Append(message.BodyHtml).Append("\r\n");
            sb.Append("--").Append(boundary).Append("--\r\n");
        }
        else if (hasHtml)
        {
            sb.Append("Content-Type: text/html; charset=utf-8\r\n\r\n");
            sb.Append(message.BodyHtml);
        }
        else
        {
            sb.Append("Content-Type: text/plain; charset=utf-8\r\n\r\n");
            sb.Append(message.BodyText);
        }

        return sb.ToString();
    }

    private static string FormatAddress(string address, string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return address;
        // Quote the display name conservatively so addresses with commas/quotes don't break the header.
        var quoted = displayName.Replace("\"", "\\\"");
        return $"\"{quoted}\" <{address}>";
    }
}
