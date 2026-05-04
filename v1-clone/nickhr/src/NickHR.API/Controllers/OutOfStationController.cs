using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NickHR.Core.DTOs;
using NickHR.Core.Enums;
using NickHR.Services.OutOfStation;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/out-of-station")]
[Authorize]
public class OutOfStationController : ControllerBase
{
    private readonly OutOfStationService _svc;

    public OutOfStationController(OutOfStationService svc)
    {
        _svc = svc;
    }

    // ─── Rate endpoints ────────────────────────────────────────────────────

    [HttpGet("rates")]
    public async Task<IActionResult> GetRates()
    {
        var rates = await _svc.GetRatesAsync();
        return Ok(ApiResponse<object>.Ok(rates));
    }

    [HttpPost("rates")]
    [Authorize(Roles = RoleSets.SeniorHR)]
    public async Task<IActionResult> CreateOrUpdateRate([FromBody] OutOfStationRateRequest req)
    {
        var rate = await _svc.CreateOrUpdateRateAsync(
            req.GradeId, req.DestinationType,
            req.AccommodationRate, req.FeedingRate,
            req.TransportRoadRate, req.TransportAirRate,
            req.MiscellaneousRate);
        return Ok(ApiResponse<object>.Ok(rate));
    }

    [HttpPost("rates/seed")]
    [Authorize(Roles = RoleSets.SeniorHR)]
    public async Task<IActionResult> SeedRates()
    {
        await _svc.SeedDefaultRatesAsync();
        return Ok(ApiResponse<object>.Ok(null, "Default rates seeded."));
    }

    // ─── Calculation & Request endpoints ──────────────────────────────────

    [HttpPost("calculate")]
    public async Task<IActionResult> Calculate([FromBody] OutOfStationCalculateRequest req)
    {
        var breakdown = await _svc.CalculateAllowanceAsync(
            req.EmployeeId, req.DestinationType,
            req.DepartureDate, req.ReturnDate, req.TransportMode);
        return Ok(ApiResponse<object>.Ok(breakdown));
    }

    [HttpPost("request")]
    public async Task<IActionResult> SubmitRequest([FromBody] OutOfStationSubmitRequest req)
    {
        var result = await _svc.SubmitRequestAsync(
            req.EmployeeId, req.Destination, req.DestinationType,
            req.Purpose, req.DepartureDate, req.ReturnDate, req.TransportMode);
        return Ok(ApiResponse<object>.Ok(result));
    }

    [HttpGet("my/{employeeId}")]
    public async Task<IActionResult> GetMyRequests(int employeeId)
    {
        var requests = await _svc.GetMyRequestsAsync(employeeId);
        return Ok(ApiResponse<object>.Ok(requests));
    }

    [HttpGet("pending")]
    [Authorize(Roles = RoleSets.HRStaffOrDeptManager)]
    public async Task<IActionResult> GetPending()
    {
        var requests = await _svc.GetPendingApprovalsAsync();
        return Ok(ApiResponse<object>.Ok(requests));
    }

    [HttpPost("{id}/approve")]
    [Authorize(Roles = RoleSets.HRStaffOrDeptManager)]
    public async Task<IActionResult> Approve(int id, [FromBody] OutOfStationApproveRequest req)
    {
        await _svc.ApproveAsync(id, req.ApproverId, req.AdvanceAmount);
        return Ok(ApiResponse<object>.Ok(null, "Request approved."));
    }

    [HttpPost("{id}/reject")]
    [Authorize(Roles = RoleSets.HRStaffOrDeptManager)]
    public async Task<IActionResult> Reject(int id, [FromBody] OutOfStationRejectRequest req)
    {
        await _svc.RejectAsync(id, req.ApproverId, req.Reason);
        return Ok(ApiResponse<object>.Ok(null, "Request rejected."));
    }

    [HttpPost("{id}/complete")]
    [Authorize(Roles = RoleSets.HRStaffOrDeptManager)]
    public async Task<IActionResult> Complete(int id)
    {
        await _svc.CompleteAsync(id);
        return Ok(ApiResponse<object>.Ok(null, "Marked as completed."));
    }

    [HttpPost("{id}/settle")]
    [Authorize(Roles = RoleSets.HRStaffOrPayroll)]
    public async Task<IActionResult> Settle(int id, [FromBody] OutOfStationSettleRequest req)
    {
        await _svc.SettleAsync(id, req.ActualExpenses, req.ReceiptPaths);
        return Ok(ApiResponse<object>.Ok(null, "Request settled."));
    }
}

// ─── Request DTOs ──────────────────────────────────────────────────────────

public class OutOfStationRateRequest
{
    public int GradeId { get; set; }
    public OutOfStationDestType DestinationType { get; set; }
    public decimal AccommodationRate { get; set; }
    public decimal FeedingRate { get; set; }
    public decimal TransportRoadRate { get; set; }
    public decimal TransportAirRate { get; set; }
    public decimal MiscellaneousRate { get; set; }
}

public class OutOfStationCalculateRequest
{
    public int EmployeeId { get; set; }
    public OutOfStationDestType DestinationType { get; set; }
    public DateTime DepartureDate { get; set; }
    public DateTime ReturnDate { get; set; }
    public TransportMode TransportMode { get; set; }
}

public class OutOfStationSubmitRequest
{
    public int EmployeeId { get; set; }
    public string Destination { get; set; } = string.Empty;
    public OutOfStationDestType DestinationType { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public DateTime DepartureDate { get; set; }
    public DateTime ReturnDate { get; set; }
    public TransportMode TransportMode { get; set; }
}

public class OutOfStationApproveRequest
{
    public int ApproverId { get; set; }
    public decimal? AdvanceAmount { get; set; }
}

public class OutOfStationRejectRequest
{
    public int ApproverId { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public class OutOfStationSettleRequest
{
    public decimal ActualExpenses { get; set; }
    public string? ReceiptPaths { get; set; }
}
