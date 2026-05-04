namespace NickHR.Core.Interfaces;

/// <summary>
/// HTTP client for the NickComms.Gateway service. NickHR uses this for ALL email and SMS
/// transport — it owns no SMTP/Hubtel credentials directly.
/// </summary>
public interface INickCommsClient
{
    Task<NickCommsEmailResult> SendEmailAsync(
        string to,
        string subject,
        string htmlBody,
        bool isHtml = true,
        IEnumerable<NickCommsAttachment>? attachments = null,
        string? clientReference = null,
        CancellationToken ct = default);

    Task<NickCommsEmailResult> SendBulkEmailAsync(
        IEnumerable<string> recipients,
        string subject,
        string htmlBody,
        bool isHtml = true,
        IEnumerable<NickCommsAttachment>? attachments = null,
        CancellationToken ct = default);

    Task<NickCommsSmsResult> SendSmsAsync(
        string phoneNumber,
        string message,
        string? clientReference = null,
        CancellationToken ct = default);

    Task<NickCommsHistoryPage> GetHistoryAsync(
        NickCommsHistoryQuery query,
        CancellationToken ct = default);

    Task<bool> PingAsync(CancellationToken ct = default);
}

public class NickCommsAttachment
{
    public string Filename { get; set; } = string.Empty;
    public string ContentBase64 { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";

    public static NickCommsAttachment FromBytes(string filename, byte[] bytes, string contentType = "application/octet-stream")
        => new() { Filename = filename, ContentBase64 = Convert.ToBase64String(bytes), ContentType = contentType };
}

public class NickCommsEmailResult
{
    public bool Success { get; set; }
    public Guid? MessageId { get; set; }
    public Guid? BatchId { get; set; }
    public int AcceptedCount { get; set; }
    public string? ErrorMessage { get; set; }
}

public class NickCommsSmsResult
{
    public bool Success { get; set; }
    public Guid? MessageId { get; set; }
    public string? Status { get; set; }
    public decimal? Rate { get; set; }
    public string? ErrorMessage { get; set; }
}

public class NickCommsHistoryQuery
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public string? Channel { get; set; }
    public string? ClientApp { get; set; }
    public string? Recipient { get; set; }
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
}

public class NickCommsHistoryPage
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public List<NickCommsHistoryItem> Items { get; set; } = new();
}

public class NickCommsHistoryItem
{
    public Guid Id { get; set; }
    public string Channel { get; set; } = string.Empty;
    public string Recipient { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string ClientApp { get; set; } = string.Empty;
    public Guid? BatchId { get; set; }
    public decimal? Rate { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
}
