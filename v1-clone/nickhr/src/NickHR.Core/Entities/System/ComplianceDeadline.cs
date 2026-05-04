using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.System;

public class ComplianceDeadline : BaseEntity
{
    [Required, MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    public DateTime DueDate { get; set; }

    [Required, MaxLength(20)]
    public string Frequency { get; set; } = "Monthly"; // Monthly, Quarterly, Annual

    [Required, MaxLength(20)]
    public string Category { get; set; } = "SSNIT"; // SSNIT, GRA, NPRA

    [MaxLength(500)]
    public string? Description { get; set; }

    public bool IsCompleted { get; set; }

    public int? CompletedById { get; set; }

    public DateTime? CompletedAt { get; set; }

    // Navigation
    public Core.Employee? CompletedBy { get; set; }
}
