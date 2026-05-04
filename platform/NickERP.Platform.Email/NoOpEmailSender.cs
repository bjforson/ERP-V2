using Microsoft.Extensions.Logging;

namespace NickERP.Platform.Email;

/// <summary>
/// Sprint 21 / Phase A — sender that logs metadata but never delivers
/// anything. Used by tests + by deployments that explicitly want the
/// invite flow disabled (e.g. "production not yet wired to SMTP, but
/// don't crash the create-tenant path").
/// </summary>
/// <remarks>
/// Logs at <c>Information</c> with <see cref="EmailMessage.To"/> +
/// <see cref="EmailMessage.Subject"/>; never logs body content because
/// the body may contain plaintext invite tokens.
/// </remarks>
public sealed class NoOpEmailSender : IEmailSender
{
    /// <summary>Provider tag returned from <see cref="ProviderName"/>.</summary>
    public const string ProviderTag = "noop";

    private readonly ILogger<NoOpEmailSender> _logger;

    public NoOpEmailSender(ILogger<NoOpEmailSender> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string ProviderName => ProviderTag;

    /// <inheritdoc />
    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        _logger.LogInformation(
            "NoOpEmailSender swallowed email to {To} (subject={Subject}). "
            + "Wire a real provider (filesystem in dev, smtp in prod) to actually deliver.",
            message.To, message.Subject);
        return Task.CompletedTask;
    }
}
