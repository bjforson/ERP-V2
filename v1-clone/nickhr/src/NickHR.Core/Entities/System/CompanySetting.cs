using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.System;

public class CompanySetting : BaseEntity
{
    [Required]
    [MaxLength(200)]
    public string Key { get; set; } = string.Empty;

    [Required]
    public string Value { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    [Required]
    [MaxLength(100)]
    public string Category { get; set; } = string.Empty;
}
