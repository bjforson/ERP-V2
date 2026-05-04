using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.System;

public class ApprovalDelegation : BaseEntity
{
    public int DelegatorId { get; set; }

    public int DelegateId { get; set; }

    public DateTime StartDate { get; set; }

    public DateTime EndDate { get; set; }

    [MaxLength(500)]
    public string? Reason { get; set; }

    public bool IsActive { get; set; } = true;

    // Navigation
    public Core.Employee Delegator { get; set; } = null!;
    public Core.Employee Delegate { get; set; } = null!;
}
