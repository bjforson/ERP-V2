using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace NickERP.Platform.Email;

/// <summary>
/// Sprint 21 / Phase A — DI helper. <c>AddNickErpEmail</c> binds
/// <see cref="EmailOptions"/> from configuration and registers the
/// configured <see cref="IEmailSender"/> + a default
/// <see cref="IEmailTemplateProvider"/>.
/// </summary>
/// <remarks>
/// Provider selection:
/// <list type="bullet">
///   <item><description><c>filesystem</c> — <see cref="FileSystemEmailSender"/>. Default in <c>Development</c>.</description></item>
///   <item><description><c>smtp</c> — <see cref="SmtpEmailSender"/>. Recommended for production.</description></item>
///   <item><description><c>noop</c> — <see cref="NoOpEmailSender"/>. Tests + explicitly-disabled deployments.</description></item>
/// </list>
/// When <c>Email:Provider</c> is unset, the default is <c>filesystem</c>
/// — same as the v0 spec. Operators who want stricter posture flip
/// to <c>noop</c> in <c>Production</c> until SMTP is wired.
/// </remarks>
public static class EmailServiceCollectionExtensions
{
    /// <summary>
    /// Bind options + register the configured sender + template
    /// provider. Idempotent — calling twice is harmless.
    /// </summary>
    public static IServiceCollection AddNickErpEmail(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<EmailOptions>()
            .Bind(configuration.GetSection(EmailOptions.SectionName))
            .ValidateOnStart();

        var providerTag = ResolveProviderTag(configuration, environment);

        switch (providerTag)
        {
            case SmtpEmailSender.ProviderTag:
                services.TryAddSingleton<IEmailSender, SmtpEmailSender>();
                break;
            case NoOpEmailSender.ProviderTag:
                services.TryAddSingleton<IEmailSender, NoOpEmailSender>();
                break;
            case FileSystemEmailSender.ProviderTag:
            default:
                services.TryAddSingleton<IEmailSender, FileSystemEmailSender>();
                break;
        }

        services.TryAddSingleton<IEmailTemplateProvider, EmbeddedResourceEmailTemplateProvider>();

        return services;
    }

    /// <summary>
    /// Resolve the provider tag from config, falling back to
    /// <c>filesystem</c> in Development and <c>noop</c> elsewhere
    /// when unset (so a freshly-deployed prod box doesn't try to
    /// write .eml files into its working directory).
    /// </summary>
    private static string ResolveProviderTag(IConfiguration configuration, IHostEnvironment? environment)
    {
        var configured = configuration[$"{EmailOptions.SectionName}:Provider"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim().ToLowerInvariant();
        }
        // Sensible default per environment: dev = filesystem (visible),
        // anything else = noop (silent — operators must opt in to a real provider).
        if (environment is not null && environment.IsDevelopment())
        {
            return FileSystemEmailSender.ProviderTag;
        }
        return NoOpEmailSender.ProviderTag;
    }
}
