using NickHR.Core.DTOs.Payroll;

namespace NickHR.Core.Interfaces;

public interface IPayrollService
{
    Task<PayrollRunDto> RunPayrollAsync(int month, int year);
    Task<PayrollRunDto?> GetPayrollRunAsync(int id);
    Task<PayslipDto?> GetPayslipAsync(int payrollRunId, int employeeId);
    Task<PayrollRunDto> LockPayrollAsync(int id);
    Task<PayrollRunDto> ReversePayrollAsync(int id);
    Task<SSNITReportDto> GenerateSSNITReportAsync(int month, int year);
    Task<byte[]> GeneratePAYEReportAsync(int month, int year);
}
