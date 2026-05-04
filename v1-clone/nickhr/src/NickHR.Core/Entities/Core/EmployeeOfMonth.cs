namespace NickHR.Core.Entities.Core;

public class EmployeeOfMonth : BaseEntity
{
    public int EmployeeId { get; set; }

    public int Month { get; set; }

    public int Year { get; set; }

    public int Votes { get; set; }

    public int? NominatedById { get; set; }

    public bool IsWinner { get; set; }

    // Navigation
    public Employee Employee { get; set; } = null!;
    public Employee? NominatedBy { get; set; }
}
