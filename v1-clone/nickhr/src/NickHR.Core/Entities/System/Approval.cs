using NickHR.Core.Entities.Core;
using NickHR.Core.Enums;
using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.System;

public class Approval : BaseEntity
{
    public int? WorkflowStepId { get; set; }

    [Required]
    [MaxLength(200)]
    public string EntityType { get; set; } = string.Empty;

    public int EntityId { get; set; }

    public int ApproverId { get; set; }

    public ApprovalStatus Status { get; set; }

    [MaxLength(1000)]
    public string? Comments { get; set; }

    public DateTime? ActionDate { get; set; }

    // Navigation Properties
    public WorkflowStep? WorkflowStep { get; set; }

    public Employee Approver { get; set; } = null!;
}
