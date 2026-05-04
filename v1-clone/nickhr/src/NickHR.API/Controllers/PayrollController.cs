using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NickHR.Core.DTOs;
using NickHR.Core.Interfaces;
using NickHR.Services.Payroll;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = RoleSets.SeniorHROrPayroll)]
public class PayrollController : ControllerBase
{
    private readonly IPayrollProcessingService _payrollService;
    private readonly IPayslipEmailService _payslipEmailService;
    private readonly ICurrentUserService _currentUser;
    private readonly MobileMoneyPaymentService _mobileMoneyPaymentService;

    public PayrollController(
        IPayrollProcessingService payrollService,
        IPayslipEmailService payslipEmailService,
        ICurrentUserService currentUser,
        MobileMoneyPaymentService mobileMoneyPaymentService)
    {
        _payrollService = payrollService;
        _payslipEmailService = payslipEmailService;
        _currentUser = currentUser;
        _mobileMoneyPaymentService = mobileMoneyPaymentService;
    }

    /// <summary>Run payroll for a given month/year.</summary>
    [HttpPost("run")]
    public async Task<IActionResult> RunPayroll([FromBody] RunPayrollRequest request)
    {
        try
        {
            var result = await _payrollService.RunPayrollAsync(
                request.Month, request.Year, _currentUser.UserName);
            return Ok(ApiResponse<object>.Ok(new
            {
                result.Id,
                result.PayPeriodMonth,
                result.PayPeriodYear,
                result.Status,
                result.TotalGrossPay,
                result.TotalNetPay,
                result.TotalSSNITEmployee,
                result.TotalSSNITEmployer,
                result.TotalPAYE,
                EmployeeCount = result.PayrollItems?.Count ?? 0
            }, "Payroll processed successfully."));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Preview payroll for a single employee.</summary>
    [HttpGet("preview/{employeeId}")]
    public async Task<IActionResult> PreviewPayroll(int employeeId, [FromQuery] int month, [FromQuery] int year)
    {
        // IDOR guard: even though the controller [Authorize] limits this action to
        // privileged roles, the per-employee scope must still be confirmed so a
        // future relaxation of the class-level policy doesn't quietly leak data.
        if (!await _currentUser.CanAccessEmployeeAsync(employeeId,
                "SuperAdmin", "HRManager", "HROfficer", "PayrollAdmin"))
        {
            return Forbid();
        }

        try
        {
            var result = await _payrollService.PreviewEmployeePayrollAsync(employeeId, month, year);
            return Ok(ApiResponse<object>.Ok(result));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Get payroll run details.</summary>
    [HttpGet("runs/{id}")]
    public async Task<IActionResult> GetPayrollRun(int id)
    {
        var run = await _payrollService.GetPayrollRunAsync(id);
        if (run == null) return NotFound(ApiResponse<object>.Fail("Payroll run not found."));
        return Ok(ApiResponse<object>.Ok(run));
    }

    /// <summary>Get payroll history.</summary>
    [HttpGet("history")]
    public async Task<IActionResult> GetHistory()
    {
        var runs = await _payrollService.GetPayrollHistoryAsync();
        return Ok(ApiResponse<object>.Ok(runs));
    }

    /// <summary>Lock payroll run.</summary>
    [HttpPost("runs/{id}/lock")]
    public async Task<IActionResult> LockPayroll(int id)
    {
        try
        {
            await _payrollService.LockPayrollAsync(id, _currentUser.UserName);
            return Ok(ApiResponse<object>.Ok(null, "Payroll locked successfully."));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Reverse payroll run.</summary>
    [HttpPost("runs/{id}/reverse")]
    public async Task<IActionResult> ReversePayroll(int id)
    {
        try
        {
            await _payrollService.ReversePayrollAsync(id, _currentUser.UserName);
            return Ok(ApiResponse<object>.Ok(null, "Payroll reversed."));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Get payslip data for an employee.</summary>
    /// <remarks>
    /// Self-access OR privileged role. Without this guard ANY authenticated user
    /// (including the requesting employee) could fetch any other employee's
    /// payslip by changing the URL — classic IDOR.
    /// </remarks>
    [HttpGet("payslip/{payrollRunId}/{employeeId}")]
    [Authorize]
    public async Task<IActionResult> GetPayslip(int payrollRunId, int employeeId)
    {
        if (!await _currentUser.CanAccessEmployeeAsync(employeeId,
                "SuperAdmin", "HRManager", "HROfficer", "PayrollAdmin"))
        {
            return Forbid();
        }

        var payslip = await _payrollService.GetPayslipAsync(payrollRunId, employeeId);
        if (payslip == null) return NotFound(ApiResponse<object>.Fail("Payslip not found."));
        return Ok(ApiResponse<object>.Ok(payslip));
    }

    /// <summary>Download payslip PDF.</summary>
    [HttpGet("payslip/{payrollRunId}/{employeeId}/pdf")]
    [Authorize]
    public async Task<IActionResult> DownloadPayslipPdf(int payrollRunId, int employeeId)
    {
        if (!await _currentUser.CanAccessEmployeeAsync(employeeId,
                "SuperAdmin", "HRManager", "HROfficer", "PayrollAdmin"))
        {
            return Forbid();
        }

        var run = await _payrollService.GetPayrollRunAsync(payrollRunId);
        if (run == null) return NotFound();

        var payslip = await _payrollService.GetPayslipAsync(payrollRunId, employeeId);
        if (payslip == null) return NotFound();

        var pdf = PayslipPdfGenerator.Generate(payslip, run.PayPeriodMonth, run.PayPeriodYear);
        return File(pdf, "application/pdf",
            $"Payslip_{payslip.EmployeeCode}_{run.PayPeriodYear}_{run.PayPeriodMonth:D2}.pdf");
    }

    /// <summary>Download all payslips for a payroll run as a single PDF.</summary>
    [HttpGet("runs/{id}/payslips-pdf")]
    public async Task<IActionResult> DownloadAllPayslipsPdf(int id)
    {
        var run = await _payrollService.GetPayrollRunAsync(id);
        if (run == null) return NotFound();

        var payslips = new List<NickHR.Services.Payroll.Models.PayrollCalculationResult>();
        foreach (var item in run.PayrollItems)
        {
            var payslip = await _payrollService.GetPayslipAsync(id, item.EmployeeId);
            if (payslip != null) payslips.Add(payslip);
        }

        var pdf = PayslipPdfGenerator.GenerateBatch(payslips, run.PayPeriodMonth, run.PayPeriodYear);
        return File(pdf, "application/pdf",
            $"Payslips_All_{run.PayPeriodYear}_{run.PayPeriodMonth:D2}.pdf");
    }

    /// <summary>Email payslips to all employees in a payroll run.</summary>
    [HttpPost("runs/{id}/email-payslips")]
    public async Task<IActionResult> EmailAllPayslips(int id)
    {
        try
        {
            var result = await _payslipEmailService.SendAllPayslipsAsync(id);
            return Ok(ApiResponse<object>.Ok(new
            {
                result.Sent,
                result.Failed,
                result.Errors
            }, $"Payslip emails: {result.Sent} sent, {result.Failed} failed."));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Email payslip to a single employee.</summary>
    [HttpPost("payslip/{runId}/{employeeId}/email")]
    public async Task<IActionResult> EmailPayslip(int runId, int employeeId)
    {
        try
        {
            await _payslipEmailService.SendPayslipAsync(runId, employeeId);
            return Ok(ApiResponse<object>.Ok(null, "Payslip emailed successfully."));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Generate SSNIT monthly report.</summary>
    [HttpGet("reports/ssnit")]
    public async Task<IActionResult> GetSSNITReport([FromQuery] int month, [FromQuery] int year)
    {
        try
        {
            var report = await _payrollService.GenerateSSNITReportAsync(month, year);
            return Ok(ApiResponse<object>.Ok(report));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Generate GRA PAYE monthly report.</summary>
    [HttpGet("reports/paye")]
    public async Task<IActionResult> GetPAYEReport([FromQuery] int month, [FromQuery] int year)
    {
        try
        {
            var report = await _payrollService.GeneratePAYEReportAsync(month, year);
            return Ok(ApiResponse<object>.Ok(report));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Generate Tier 2 pension monthly report.</summary>
    [HttpGet("reports/tier2")]
    public async Task<IActionResult> GetTier2Report([FromQuery] int month, [FromQuery] int year)
    {
        try
        {
            var report = await _payrollService.GenerateTier2ReportAsync(month, year);
            return Ok(ApiResponse<object>.Ok(report));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Generate Tier 3 pension monthly report.</summary>
    [HttpGet("reports/tier3")]
    public async Task<IActionResult> GetTier3Report([FromQuery] int month, [FromQuery] int year)
    {
        try
        {
            var report = await _payrollService.GenerateTier3ReportAsync(month, year);
            return Ok(ApiResponse<object>.Ok(report));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Generate GRA annual return for a full year.</summary>
    [HttpGet("reports/annual-return")]
    public async Task<IActionResult> GetAnnualReturn([FromQuery] int year)
    {
        try
        {
            var report = await _payrollService.GenerateAnnualReturnAsync(year);
            return Ok(ApiResponse<object>.Ok(report));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Generate Mobile Money payment file for a payroll run.</summary>
    [HttpGet("runs/{id}/momo-file")]
    public async Task<IActionResult> GetMoMoFile(int id, [FromQuery] string network = "MTN")
    {
        try
        {
            var bytes = await _mobileMoneyPaymentService.GenerateMoMoPaymentFileAsync(id, network);
            var fileName = $"MoMo_{network}_{id}_{DateTime.UtcNow:yyyyMMdd}.csv";
            return File(bytes, "text/csv", fileName);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Generate bank transfer payment file for a payroll run.</summary>
    [HttpGet("runs/{id}/bank-file")]
    public async Task<IActionResult> GetBankFile(int id)
    {
        try
        {
            var bytes = await _mobileMoneyPaymentService.GenerateBankPaymentFileAsync(id);
            var fileName = $"BankTransfer_{id}_{DateTime.UtcNow:yyyyMMdd}.csv";
            return File(bytes, "text/csv", fileName);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }
}

public class RunPayrollRequest
{
    public int Month { get; set; }
    public int Year { get; set; }
}
