using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickHR.Core.Interfaces;
using NickHR.Infrastructure.Data;
using NickHR.Services.Payroll.Models;

namespace NickHR.Services.Payroll;

public interface IPayslipEmailService
{
    Task<PayslipEmailResult> SendAllPayslipsAsync(int payrollRunId);
    Task SendPayslipAsync(int payrollRunId, int employeeId);
}

public class PayslipEmailResult
{
    public int Sent { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; set; } = new();
}

public class PayslipEmailService : IPayslipEmailService
{
    private readonly NickHRDbContext _db;
    private readonly IPayrollProcessingService _payrollService;
    private readonly IEmailService _email;
    private readonly ILogger<PayslipEmailService> _logger;

    public PayslipEmailService(
        NickHRDbContext db,
        IPayrollProcessingService payrollService,
        IEmailService email,
        ILogger<PayslipEmailService> logger)
    {
        _db = db;
        _payrollService = payrollService;
        _email = email;
        _logger = logger;
    }

    public async Task<PayslipEmailResult> SendAllPayslipsAsync(int payrollRunId)
    {
        var run = await _payrollService.GetPayrollRunAsync(payrollRunId)
            ?? throw new KeyNotFoundException($"Payroll run {payrollRunId} not found.");

        var result = new PayslipEmailResult();

        foreach (var item in run.PayrollItems)
        {
            try
            {
                await SendPayslipAsync(payrollRunId, item.EmployeeId);
                result.Sent++;
            }
            catch (Exception ex)
            {
                result.Failed++;
                var errorMsg = $"Employee {item.EmployeeId}: {ex.Message}";
                result.Errors.Add(errorMsg);
                _logger.LogError(ex, "Failed to send payslip for employee {EmployeeId} in run {RunId}",
                    item.EmployeeId, payrollRunId);
            }
        }

        _logger.LogInformation("Payslip email batch for run {RunId}: {Sent} sent, {Failed} failed",
            payrollRunId, result.Sent, result.Failed);

        return result;
    }

    public async Task SendPayslipAsync(int payrollRunId, int employeeId)
    {
        var run = await _payrollService.GetPayrollRunAsync(payrollRunId)
            ?? throw new KeyNotFoundException($"Payroll run {payrollRunId} not found.");

        var payslip = await _payrollService.GetPayslipAsync(payrollRunId, employeeId)
            ?? throw new KeyNotFoundException($"Payslip not found for employee {employeeId} in run {payrollRunId}.");

        var employee = await _db.Employees.FindAsync(employeeId)
            ?? throw new KeyNotFoundException($"Employee {employeeId} not found.");

        var recipientEmail = employee.WorkEmail ?? employee.PersonalEmail;
        if (string.IsNullOrEmpty(recipientEmail))
        {
            _logger.LogWarning("Employee {EmployeeId} has no email address. Skipping payslip.", employeeId);
            return;
        }

        var pdf = PayslipPdfGenerator.Generate(payslip, run.PayPeriodMonth, run.PayPeriodYear);
        var monthName = new DateTime(run.PayPeriodYear, run.PayPeriodMonth, 1).ToString("MMMM yyyy");

        await SendEmailWithAttachmentAsync(
            to: recipientEmail,
            subject: $"Your Payslip for {monthName}",
            htmlBody: BuildPayslipEmailBody(payslip, monthName),
            attachmentName: $"Payslip_{payslip.EmployeeCode}_{run.PayPeriodYear}_{run.PayPeriodMonth:D2}.pdf",
            attachmentData: pdf
        );
    }

    private string BuildPayslipEmailBody(PayrollCalculationResult payslip, string monthName)
    {
        return $@"
<html>
<body style=""font-family: Arial, sans-serif; color: #333;"">
    <h2>Payslip for {monthName}</h2>
    <p>Dear {payslip.EmployeeName},</p>
    <p>Please find attached your payslip for <strong>{monthName}</strong>.</p>
    <table style=""border-collapse: collapse; width: 300px;"">
        <tr><td style=""padding: 4px 8px;""><strong>Gross Pay:</strong></td><td style=""padding: 4px 8px; text-align: right;"">GHS {payslip.GrossPay:N2}</td></tr>
        <tr><td style=""padding: 4px 8px;""><strong>Net Pay:</strong></td><td style=""padding: 4px 8px; text-align: right;"">GHS {payslip.NetPay:N2}</td></tr>
    </table>
    <br/>
    <p>This is an automated message. Please do not reply to this email.</p>
    <p>For queries, contact HR.</p>
</body>
</html>";
    }

    private async Task SendEmailWithAttachmentAsync(
        string to, string subject, string htmlBody,
        string attachmentName, byte[] attachmentData)
    {
        var attachment = NickCommsAttachment.FromBytes(attachmentName, attachmentData, "application/pdf");
        await _email.SendAsync(to, subject, htmlBody, new[] { attachment });
    }
}
