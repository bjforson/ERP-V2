using Microsoft.Extensions.Logging;

namespace NickHR.Services.Communication;

public interface IWhatsAppService
{
    Task<bool> SendMessageAsync(string phoneNumber, string message);
    Task<bool> SendDocumentAsync(string phoneNumber, string documentPath, string caption);
    List<WhatsAppLogEntry> GetRecentLogs(int count = 50);
}

public class WhatsAppService : IWhatsAppService
{
    private readonly ILogger<WhatsAppService> _logger;
    private static readonly List<WhatsAppLogEntry> _messageLog = new();

    public WhatsAppService(ILogger<WhatsAppService> logger)
    {
        _logger = logger;
    }

    public Task<bool> SendMessageAsync(string phoneNumber, string message)
    {
        _logger.LogInformation("WhatsApp [LOG-ONLY] to {Phone}: {Message}", phoneNumber, message);

        _messageLog.Add(new WhatsAppLogEntry
        {
            PhoneNumber = phoneNumber,
            Message = message,
            Type = "Text",
            SentAt = DateTime.UtcNow,
            Status = "Logged (not sent - API not configured)"
        });

        if (_messageLog.Count > 500)
            _messageLog.RemoveRange(0, _messageLog.Count - 500);

        return Task.FromResult(true);
    }

    public Task<bool> SendDocumentAsync(string phoneNumber, string documentPath, string caption)
    {
        _logger.LogInformation("WhatsApp Document [LOG-ONLY] to {Phone}: {Path} - {Caption}",
            phoneNumber, documentPath, caption);

        _messageLog.Add(new WhatsAppLogEntry
        {
            PhoneNumber = phoneNumber,
            Message = caption,
            Type = "Document",
            DocumentPath = documentPath,
            SentAt = DateTime.UtcNow,
            Status = "Logged (not sent - API not configured)"
        });

        return Task.FromResult(true);
    }

    public List<WhatsAppLogEntry> GetRecentLogs(int count = 50)
    {
        return _messageLog.OrderByDescending(l => l.SentAt).Take(count).ToList();
    }
}

public class WhatsAppLogEntry
{
    public string PhoneNumber { get; set; } = "";
    public string Message { get; set; } = "";
    public string Type { get; set; } = "Text";
    public string? DocumentPath { get; set; }
    public DateTime SentAt { get; set; }
    public string Status { get; set; } = "";
}
