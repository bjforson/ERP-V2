using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.System;

public class GeneratedLetter : BaseEntity
{
    public int LetterTemplateId { get; set; }

    public int EmployeeId { get; set; }

    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    public int? GeneratedById { get; set; }

    [MaxLength(500)]
    public string? PdfPath { get; set; }

    // Navigation
    public LetterTemplate LetterTemplate { get; set; } = null!;
    public Core.Employee Employee { get; set; } = null!;
    public Core.Employee? GeneratedBy { get; set; }
}
