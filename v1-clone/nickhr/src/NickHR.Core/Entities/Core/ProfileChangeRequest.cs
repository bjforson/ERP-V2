using NickHR.Core.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Core;

public class ProfileChangeRequest : BaseEntity
{
    // Who is requesting
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    // What field is being changed
    [Required]
    [MaxLength(100)]
    public string FieldName { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string FieldLabel { get; set; } = string.Empty;

    // Old and new values as strings (serialized if needed)
    [MaxLength(1000)]
    public string? OldValue { get; set; }

    [MaxLength(1000)]
    public string? NewValue { get; set; }

    // Employee's reason for the change
    [MaxLength(500)]
    public string? Reason { get; set; }

    // 1 = Auto-approve (Tier 1), 2 = Needs HR approval (Tier 2)
    public int Tier { get; set; }

    // Current status
    public ChangeRequestStatus Status { get; set; } = ChangeRequestStatus.Pending;

    // Review info (set when HR approves/rejects)
    public int? ReviewedById { get; set; }
    public Employee? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }

    [MaxLength(500)]
    public string? RejectionReason { get; set; }

    // When the change was actually written to the Employee record
    public DateTime? AppliedAt { get; set; }
}
