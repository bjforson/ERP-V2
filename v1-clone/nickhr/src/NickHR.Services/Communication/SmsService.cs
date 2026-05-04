using Microsoft.Extensions.Logging;
using NickHR.Core.Interfaces;

namespace NickHR.Services.Communication;

public interface ISmsService
{
    Task<bool> SendAsync(string phoneNumber, string message);
    Task<SmsResult> SendBulkAsync(IEnumerable<(string Phone, string Message)> messages);
    List<SmsLogEntry> GetRecentLogs(int count = 50);
}

/// <summary>
/// SMS service backed by NickComms.Gateway (Hubtel). Maintains a small in-memory ring buffer
/// of recent sends for quick admin display; full history is queryable via the gateway.
/// </summary>
public class SmsService : ISmsService
{
    private readonly INickCommsClient _comms;
    private readonly ILogger<SmsService> _logger;
    private static readonly List<SmsLogEntry> _messageLog = new();
    private static readonly object _lock = new();

    public SmsService(INickCommsClient comms, ILogger<SmsService> logger)
    {
        _comms = comms;
        _logger = logger;
    }

    public async Task<bool> SendAsync(string phoneNumber, string message)
    {
        var result = await _comms.SendSmsAsync(phoneNumber, message);

        var entry = new SmsLogEntry
        {
            PhoneNumber = phoneNumber,
            Message = message,
            SentAt = DateTime.UtcNow,
            Status = result.Success ? (result.Status ?? "queued") : $"failed: {result.ErrorMessage}"
        };

        lock (_lock)
        {
            _messageLog.Add(entry);
            if (_messageLog.Count > 500)
                _messageLog.RemoveRange(0, _messageLog.Count - 500);
        }

        if (result.Success)
            _logger.LogInformation("SMS queued via NickComms ({Id}) to {Phone}", result.MessageId, phoneNumber);
        else
            _logger.LogWarning("SMS send failed for {Phone}: {Error}", phoneNumber, result.ErrorMessage);

        return result.Success;
    }

    public async Task<SmsResult> SendBulkAsync(IEnumerable<(string Phone, string Message)> messages)
    {
        var sent = 0;
        var failed = 0;

        foreach (var (phone, message) in messages)
        {
            var success = await SendAsync(phone, message);
            if (success) sent++;
            else failed++;
        }

        return new SmsResult { Sent = sent, Failed = failed };
    }

    public List<SmsLogEntry> GetRecentLogs(int count = 50)
    {
        lock (_lock)
        {
            return _messageLog.OrderByDescending(l => l.SentAt).Take(count).ToList();
        }
    }
}

public class SmsResult
{
    public int Sent { get; set; }
    public int Failed { get; set; }
}

public class SmsLogEntry
{
    public string PhoneNumber { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTime SentAt { get; set; }
    public string Status { get; set; } = "";
}
