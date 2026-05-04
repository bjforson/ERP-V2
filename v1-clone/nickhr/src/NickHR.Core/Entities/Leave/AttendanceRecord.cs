using NickHR.Core.Entities.Core;
using NickHR.Core.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Leave;

public class AttendanceRecord : BaseEntity
{
    public int EmployeeId { get; set; }

    public DateTime Date { get; set; }

    public DateTime? ClockIn { get; set; }

    public DateTime? ClockOut { get; set; }

    public AttendanceType AttendanceType { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal? WorkHours { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal? OvertimeHours { get; set; }

    [MaxLength(50)]
    public string? IPAddress { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    // Navigation Properties
    public Employee Employee { get; set; } = null!;
}
