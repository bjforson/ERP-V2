using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.System;

public class LetterTemplate : BaseEntity
{
    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required, MaxLength(50)]
    public string Code { get; set; } = string.Empty;

    [MaxLength(100)]
    public string Category { get; set; } = "General";

    public string HtmlBody { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
}
