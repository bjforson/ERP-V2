using NickHR.Core.Enums;
using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.Core;

public class Recognition : BaseEntity
{
    public int SenderEmployeeId { get; set; }

    public int RecipientEmployeeId { get; set; }

    [Required]
    [MaxLength(500)]
    public string Message { get; set; } = string.Empty;

    public RecognitionCategory Category { get; set; }

    public int Points { get; set; } = 10;

    public bool IsPublic { get; set; } = true;

    // Navigation
    public Employee SenderEmployee { get; set; } = null!;
    public Employee RecipientEmployee { get; set; } = null!;
}
