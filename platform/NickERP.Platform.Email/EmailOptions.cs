namespace NickERP.Platform.Email;

/// <summary>
/// Sprint 21 / Phase A — strongly-typed options bound from the
/// <c>Email</c> configuration section.
/// </summary>
/// <remarks>
/// Sample <c>appsettings.Development.json</c>:
/// <code>
/// {
///   "Email": {
///     "Provider": "filesystem",
///     "DefaultFrom": "no-reply@nickerp.local",
///     "Invite": {
///       "DefaultExpiryHours": 72,
///       "PortalBaseUrl": "http://localhost:5400"
///     },
///     "FileSystem": {
///       "OutboxDirectory": "var/email-outbox"
///     }
///   }
/// }
/// </code>
/// Production switches <c>Provider</c> to <c>smtp</c> and adds a
/// <c>Smtp</c> section. Env-var override per ASP.NET conventions:
/// <c>Email__Smtp__Host</c>, <c>Email__Smtp__Password</c>, etc.
/// </remarks>
public sealed class EmailOptions
{
    /// <summary>Configuration section name. Bind with <c>Configuration.GetSection(EmailOptions.SectionName)</c>.</summary>
    public const string SectionName = "Email";

    /// <summary>Provider tag. One of <c>filesystem</c>, <c>smtp</c>, <c>noop</c>. Defaults to <c>filesystem</c>.</summary>
    public string Provider { get; set; } = FileSystemEmailSender.ProviderTag;

    /// <summary>Default <c>From:</c> address used when an <see cref="EmailMessage"/> doesn't specify one. Required for the SMTP sender; advisory for the filesystem sender.</summary>
    public string? DefaultFrom { get; set; }

    /// <summary>Filesystem-sender-specific settings.</summary>
    public FileSystemSenderOptions? FileSystem { get; set; }

    /// <summary>SMTP-sender-specific settings.</summary>
    public SmtpOptions? Smtp { get; set; }

    /// <summary>Invite-flow tunables.</summary>
    public InviteOptions Invite { get; set; } = new();

    /// <summary><see cref="FileSystemEmailSender"/> options.</summary>
    public sealed class FileSystemSenderOptions
    {
        /// <summary>Where to drop the .eml files. Relative to <c>Directory.GetCurrentDirectory()</c> when not absolute. Default <c>var/email-outbox</c>.</summary>
        public string? OutboxDirectory { get; set; }
    }

    /// <summary><see cref="SmtpEmailSender"/> options.</summary>
    public sealed class SmtpOptions
    {
        /// <summary>Relay host. Required for the smtp provider.</summary>
        public string? Host { get; set; }

        /// <summary>SMTP port. Default 587 (STARTTLS); 465 = implicit TLS; 25 = relay (no auth).</summary>
        public int Port { get; set; } = 587;

        /// <summary>SASL username. Optional; when null no AUTH is performed.</summary>
        public string? Username { get; set; }

        /// <summary>SASL password. Source from env var (<c>Email__Smtp__Password</c>); avoid plaintext appsettings.</summary>
        public string? Password { get; set; }

        /// <summary>
        /// Default <c>true</c>. When <c>false</c>, the sender refuses
        /// to connect unless <see cref="AllowInsecure"/> is also
        /// <c>true</c> — preventing accidental plaintext SMTP.
        /// </summary>
        public bool UseTls { get; set; } = true;

        /// <summary>
        /// Documented escape hatch for the very small set of legitimate
        /// plaintext-relay scenarios (internal mail-relays on a
        /// hardened LAN). Pairs with <see cref="UseTls"/>=<c>false</c>;
        /// alone it does nothing. Logs a warning per send when active.
        /// </summary>
        public bool AllowInsecure { get; set; }
    }

    /// <summary>Invite-flow tunables (consumed by <c>InviteService</c>).</summary>
    public sealed class InviteOptions
    {
        /// <summary>Default invite TTL when <c>InviteService.IssueInviteAsync</c> isn't given an explicit one. Default 72 hours.</summary>
        public int DefaultExpiryHours { get; set; } = 72;

        /// <summary>
        /// Public base URL of the portal that hosts the invite-acceptance
        /// page. Used to build the link in the email body.
        /// Example: <c>https://portal.nickerp.example.com</c>. Required.
        /// </summary>
        public string? PortalBaseUrl { get; set; }
    }
}
