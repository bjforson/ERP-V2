namespace NickHR.Core.Interfaces;

/// <summary>
/// Templated email service. All transport is delegated to <see cref="INickCommsClient"/>.
/// Templates are stored in NickHR's database and rendered locally before being shipped to
/// the gateway.
/// </summary>
public interface IEmailService
{
    Task SendAsync(string to, string subject, string htmlBody);
    Task SendAsync(string to, string subject, string htmlBody, IEnumerable<NickCommsAttachment> attachments);
    Task SendTemplatedAsync(string to, string templateCode, Dictionary<string, string> mergeFields);
    Task SendBulkAsync(IEnumerable<string> recipients, string subject, string htmlBody);
}
