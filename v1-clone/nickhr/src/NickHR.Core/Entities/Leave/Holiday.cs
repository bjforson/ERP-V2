using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.Leave;

public class Holiday : BaseEntity
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public DateTime Date { get; set; }

    public int Year { get; set; }

    public bool IsRecurring { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }
}
