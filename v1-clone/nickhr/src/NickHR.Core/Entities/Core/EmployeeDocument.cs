using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.Core;

public class EmployeeDocument : BaseEntity
{
    public int EmployeeId { get; set; }

    [Required]
    [MaxLength(100)]
    public string DocumentType { get; set; } = string.Empty;

    [Required]
    [MaxLength(300)]
    public string FileName { get; set; } = string.Empty;

    [Required]
    [MaxLength(1000)]
    public string FilePath { get; set; } = string.Empty;

    public long FileSize { get; set; }

    [MaxLength(100)]
    public string? ContentType { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    /// <summary>File content stored in database as byte array.</summary>
    public byte[]? FileData { get; set; }

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    public int Version { get; set; } = 1;

    // Navigation Properties
    public Employee Employee { get; set; } = null!;
}
