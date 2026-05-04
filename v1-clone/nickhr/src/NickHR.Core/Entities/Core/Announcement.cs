using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.Core;

public class Announcement : BaseEntity
{
    [Required]
    [MaxLength(300)]
    public string Title { get; set; } = string.Empty;

    [Required]
    public string Content { get; set; } = string.Empty;

    public DateTime PublishedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ExpiresAt { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Null means company-wide announcement</summary>
    public int? DepartmentId { get; set; }

    public int AuthorId { get; set; }

    // Navigation Properties
    public Employee Author { get; set; } = null!;

    public Department? Department { get; set; }
}
