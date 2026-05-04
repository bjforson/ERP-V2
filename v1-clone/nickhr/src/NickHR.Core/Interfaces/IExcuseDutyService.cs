using NickHR.Core.Enums;

namespace NickHR.Core.Interfaces;

public record ExcuseDutyDto(
    int Id,
    int EmployeeId,
    string EmployeeName,
    int? DepartmentId,
    string? DepartmentName,
    ExcuseDutyType ExcuseDutyType,
    DateTime Date,
    TimeSpan StartTime,
    TimeSpan EndTime,
    decimal DurationHours,
    string Reason,
    string? Destination,
    ExcuseDutyStatus Status,
    int? ApprovedById,
    string? ApprovedByName,
    DateTime? ApprovedAt,
    string? RejectionReason,
    string? MedicalCertificatePath,
    bool ReturnConfirmed,
    TimeSpan? ReturnTime,
    decimal? ActualDurationHours,
    DateTime CreatedAt
);

public record ExcuseDutyMonthlyReportDto(
    int EmployeeId,
    string EmployeeName,
    int? DepartmentId,
    string? DepartmentName,
    int Month,
    int Year,
    decimal TotalHours,
    int TotalCount,
    Dictionary<string, decimal> HoursByType
);

public interface IExcuseDutyService
{
    Task<ExcuseDutyDto> RequestExcuseDutyAsync(
        int employeeId,
        ExcuseDutyType type,
        DateTime date,
        TimeSpan startTime,
        TimeSpan endTime,
        string reason,
        string? destination = null,
        string? medicalCertPath = null);

    Task<List<ExcuseDutyDto>> GetMyRequestsAsync(int employeeId, int? month, int? year);
    Task<List<ExcuseDutyDto>> GetPendingApprovalsAsync();
    Task<ExcuseDutyDto> ApproveAsync(int id, int approverId);
    Task<ExcuseDutyDto> RejectAsync(int id, int approverId, string reason);
    Task<ExcuseDutyDto> ConfirmReturnAsync(int id, TimeSpan returnTime);
    Task<List<ExcuseDutyMonthlyReportDto>> GetMonthlyReportAsync(int? departmentId, int month, int year);
}
