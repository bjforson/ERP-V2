using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickHR.Core.Interfaces;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Common;

// IEmailService interface moved to NickHR.Core.Interfaces.IEmailService
// to avoid circular project references between Services and Services.Payroll.

/// <summary>
/// Templated email service for NickHR. Templates and merge-field substitution stay in this
/// class (DB-backed); actual transport is delegated to the NickComms.Gateway via
/// <see cref="INickCommsClient"/>. NickHR no longer holds SMTP credentials.
/// </summary>
public class EmailService : IEmailService
{
    private readonly INickCommsClient _comms;
    private readonly NickHRDbContext _db;
    private readonly ILogger<EmailService> _logger;

    public EmailService(INickCommsClient comms, NickHRDbContext db, ILogger<EmailService> logger)
    {
        _comms = comms;
        _db = db;
        _logger = logger;
    }

    public async Task SendAsync(string to, string subject, string htmlBody)
    {
        var result = await _comms.SendEmailAsync(to, subject, htmlBody, isHtml: true);
        if (!result.Success)
        {
            _logger.LogError("Failed to send email to {To} via NickComms: {Error}", to, result.ErrorMessage);
            throw new InvalidOperationException($"Email send failed: {result.ErrorMessage}");
        }
        _logger.LogInformation("Email queued via NickComms ({Id}) to {To}: {Subject}", result.MessageId, to, subject);
    }

    public async Task SendAsync(string to, string subject, string htmlBody, IEnumerable<NickCommsAttachment> attachments)
    {
        var result = await _comms.SendEmailAsync(to, subject, htmlBody, isHtml: true, attachments: attachments);
        if (!result.Success)
        {
            _logger.LogError("Failed to send email with attachments to {To} via NickComms: {Error}", to, result.ErrorMessage);
            throw new InvalidOperationException($"Email send failed: {result.ErrorMessage}");
        }
        _logger.LogInformation("Email + attachments queued via NickComms ({Id}) to {To}: {Subject}", result.MessageId, to, subject);
    }

    public async Task SendTemplatedAsync(string to, string templateCode, Dictionary<string, string> mergeFields)
    {
        var template = await _db.EmailTemplates
            .FirstOrDefaultAsync(t => t.Code == templateCode && t.IsActive && !t.IsDeleted);

        if (template == null)
        {
            _logger.LogWarning("Email template '{Code}' not found or inactive.", templateCode);
            return;
        }

        var subject = template.Subject;
        var body = template.Body;

        foreach (var field in mergeFields)
        {
            subject = subject.Replace($"{{{{{field.Key}}}}}", field.Value);
            body = body.Replace($"{{{{{field.Key}}}}}", field.Value);
        }

        await SendAsync(to, subject, body);
    }

    public async Task SendBulkAsync(IEnumerable<string> recipients, string subject, string htmlBody)
    {
        var list = recipients?.ToList() ?? new List<string>();
        if (list.Count == 0) return;

        var result = await _comms.SendBulkEmailAsync(list, subject, htmlBody, isHtml: true);
        if (!result.Success)
        {
            _logger.LogError("Bulk email send failed via NickComms ({Count} recipients): {Error}", list.Count, result.ErrorMessage);
            throw new InvalidOperationException($"Bulk email send failed: {result.ErrorMessage}");
        }
        _logger.LogInformation("Bulk email queued via NickComms (batch={Batch}, accepted={Count}): {Subject}",
            result.BatchId, result.AcceptedCount, subject);
    }
}
