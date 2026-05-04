using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;

namespace NickERP.Platform.Email;

/// <summary>
/// Sprint 21 / Phase A — small contract for resolving named email
/// templates. Returns the rendered subject + bodies (text + html)
/// after substituting <c>{{Placeholder}}</c> tokens.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately tiny: no Razor / no Liquid / no Mustache. The only
/// substitution syntax is literal <c>{{Key}}</c> matching against a
/// caller-supplied dictionary; missing keys throw so a typo isn't
/// silently swallowed into an empty string in the email body.
/// </para>
/// <para>
/// Implementations: <see cref="EmbeddedResourceEmailTemplateProvider"/>
/// reads from the <c>Resources/email-templates/</c> embedded folder
/// in <c>NickERP.Platform.Email.dll</c>. Tests can swap in a
/// dictionary-backed mock by implementing the interface directly.
/// </para>
/// </remarks>
public interface IEmailTemplateProvider
{
    /// <summary>
    /// Render the template named <paramref name="templateKey"/> with
    /// the given placeholder values. Throws
    /// <see cref="KeyNotFoundException"/> if the template doesn't
    /// exist; throws <see cref="InvalidOperationException"/> if the
    /// rendered output references a placeholder not in
    /// <paramref name="placeholders"/>.
    /// </summary>
    Task<RenderedEmailTemplate> RenderAsync(
        string templateKey,
        IReadOnlyDictionary<string, string> placeholders,
        CancellationToken ct = default);
}

/// <summary>
/// Output of <see cref="IEmailTemplateProvider.RenderAsync"/>. The
/// caller wires these into an <see cref="EmailMessage"/> for sending.
/// </summary>
public sealed record RenderedEmailTemplate(string Subject, string BodyText, string BodyHtml);

/// <summary>
/// Default <see cref="IEmailTemplateProvider"/>. Loads
/// <c>{templateKey}.subject.txt</c>, <c>{templateKey}.txt</c>, and
/// <c>{templateKey}.html</c> from the assembly's embedded resources
/// under <c>Resources/email-templates/</c>.
/// </summary>
/// <remarks>
/// Resource names map to file paths via the standard MSBuild rule:
/// <c>NickERP.Platform.Email.Resources.email-templates.{key}.subject.txt</c>
/// for a file <c>Resources/email-templates/{key}.subject.txt</c>.
/// </remarks>
public sealed class EmbeddedResourceEmailTemplateProvider : IEmailTemplateProvider
{
    private readonly ILogger<EmbeddedResourceEmailTemplateProvider> _logger;
    private readonly Assembly _resourcesAssembly;

    public EmbeddedResourceEmailTemplateProvider(ILogger<EmbeddedResourceEmailTemplateProvider> logger)
        : this(logger, typeof(EmbeddedResourceEmailTemplateProvider).Assembly)
    {
    }

    /// <summary>Test-friendly overload taking an explicit assembly to scan.</summary>
    public EmbeddedResourceEmailTemplateProvider(
        ILogger<EmbeddedResourceEmailTemplateProvider> logger,
        Assembly resourcesAssembly)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _resourcesAssembly = resourcesAssembly ?? throw new ArgumentNullException(nameof(resourcesAssembly));
    }

    /// <inheritdoc />
    public async Task<RenderedEmailTemplate> RenderAsync(
        string templateKey,
        IReadOnlyDictionary<string, string> placeholders,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateKey);
        ArgumentNullException.ThrowIfNull(placeholders);

        var subject = await ReadResourceAsync($"{templateKey}.subject.txt", ct);
        var text = await ReadResourceAsync($"{templateKey}.txt", ct);
        var html = await ReadResourceAsync($"{templateKey}.html", ct);

        return new RenderedEmailTemplate(
            Substitute(subject, placeholders, $"{templateKey}.subject"),
            Substitute(text, placeholders, $"{templateKey}.txt"),
            Substitute(html, placeholders, $"{templateKey}.html"));
    }

    private async Task<string> ReadResourceAsync(string resourceFileName, CancellationToken ct)
    {
        // Embedded resource names use '.' as the path separator, never '/'.
        // MSBuild's default ManifestResourceName replaces dashes in path
        // segments with underscores (the legacy CLR identifier escape).
        // We try both forms so directory renames don't silently break.
        var withDash = $"NickERP.Platform.Email.Resources.email-templates.{resourceFileName}";
        var withUnderscore = $"NickERP.Platform.Email.Resources.email_templates.{resourceFileName}";
        var stream = _resourcesAssembly.GetManifestResourceStream(withDash)
            ?? _resourcesAssembly.GetManifestResourceStream(withUnderscore);
        if (stream is null)
        {
            // Surface available resources so packagers can spot the typo.
            var available = string.Join(", ", _resourcesAssembly.GetManifestResourceNames());
            throw new KeyNotFoundException(
                $"Email template resource '{withDash}' (or {withUnderscore}) not found. Available resources: {available}");
        }
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync(ct);
        }
        finally
        {
            await stream.DisposeAsync();
        }
    }

    /// <summary>
    /// Replace <c>{{Key}}</c> tokens in <paramref name="template"/> with
    /// values from <paramref name="placeholders"/>. Missing keys throw
    /// so a renamed-without-template-update is loud.
    /// </summary>
    public static string Substitute(string template, IReadOnlyDictionary<string, string> placeholders, string sourceLabel = "(unknown)")
    {
        ArgumentNullException.ThrowIfNull(template);
        var sb = new StringBuilder(template.Length);
        int i = 0;
        while (i < template.Length)
        {
            // Look for "{{"
            if (i + 1 < template.Length && template[i] == '{' && template[i + 1] == '{')
            {
                var endIdx = template.IndexOf("}}", i + 2, StringComparison.Ordinal);
                if (endIdx < 0)
                {
                    // Unbalanced — copy literally and stop scanning.
                    sb.Append(template, i, template.Length - i);
                    break;
                }
                var key = template.Substring(i + 2, endIdx - (i + 2)).Trim();
                if (!placeholders.TryGetValue(key, out var value))
                {
                    throw new InvalidOperationException(
                        $"Email template '{sourceLabel}' references placeholder '{key}' but no value was supplied. "
                        + "Either pass the value or remove the placeholder from the template.");
                }
                sb.Append(value);
                i = endIdx + 2;
            }
            else
            {
                sb.Append(template[i]);
                i++;
            }
        }
        return sb.ToString();
    }
}
