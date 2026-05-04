using NickHR.Core.Entities.Core;

namespace NickHR.Core.Entities.System;

public class PolicyAcknowledgement : BaseEntity
{
    public int PolicyDocumentId { get; set; }
    public PolicyDocument PolicyDocument { get; set; } = null!;

    public int EmployeeId { get; set; }
    public Core.Employee Employee { get; set; } = null!;

    public DateTime AcknowledgedAt { get; set; } = DateTime.UtcNow;
}
