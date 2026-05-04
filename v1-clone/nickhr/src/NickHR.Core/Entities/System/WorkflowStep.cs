using NickHR.Core.Entities.Core;
using NickHR.Core.Enums;
using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.System;

public class WorkflowStep : BaseEntity
{
    public int WorkflowId { get; set; }

    public int StepOrder { get; set; }

    public WorkflowStepType StepType { get; set; }

    public int? ApproverRoleId { get; set; }

    public int? ApproverEmployeeId { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    // Navigation Properties
    public Workflow Workflow { get; set; } = null!;

    public Employee? ApproverEmployee { get; set; }
}
