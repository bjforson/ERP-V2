using NickHR.Core.Entities.Core;
using NickHR.Core.Enums;
using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.System;

public class Notification : BaseEntity
{
    public int RecipientEmployeeId { get; set; }

    [Required]
    [MaxLength(300)]
    public string Title { get; set; } = string.Empty;

    [Required]
    public string Message { get; set; } = string.Empty;

    public NotificationType NotificationType { get; set; }

    public bool IsRead { get; set; }

    public DateTime? ReadAt { get; set; }

    [MaxLength(200)]
    public string? RelatedEntityType { get; set; }

    [MaxLength(50)]
    public string? RelatedEntityId { get; set; }

    // Navigation Properties
    public Employee RecipientEmployee { get; set; } = null!;
}
