using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NickHR.Core.Enums;
using NickHR.Infrastructure.Data;
using NickHR.Services.ProfileChange;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/profile-changes")]
[Authorize]
public class ProfileChangeController : ControllerBase
{
    private readonly ProfileChangeService _svc;
    private readonly UserManager<ApplicationUser> _userManager;

    public ProfileChangeController(ProfileChangeService svc, UserManager<ApplicationUser> userManager)
    {
        _svc = svc;
        _userManager = userManager;
    }

    // ── Employee endpoints ────────────────────────────────────────────────────

    /// <summary>
    /// Submit a profile field change.
    /// POST /api/profile-changes/submit
    /// </summary>
    [HttpPost("submit")]
    public async Task<IActionResult> Submit([FromBody] SubmitChangeDto dto)
    {
        var appUser = await _userManager.GetUserAsync(User);
        if (appUser?.EmployeeId == null)
            return Unauthorized(new { message = "No linked employee record." });

        var isSuperAdmin = User.IsInRole("SuperAdmin");

        var request = await _svc.SubmitChangeAsync(
            appUser.EmployeeId.Value,
            dto.FieldName,
            dto.NewValue,
            dto.Reason,
            isSuperAdmin);

        return Ok(new
        {
            request.Id,
            request.FieldName,
            request.FieldLabel,
            request.Status,
            request.Tier,
            request.AppliedAt,
            message = request.Status == ChangeRequestStatus.Approved
                ? $"{request.FieldLabel} updated."
                : $"Change to {request.FieldLabel} submitted for HR approval."
        });
    }

    /// <summary>
    /// Get the logged-in employee's change history.
    /// GET /api/profile-changes/my
    /// </summary>
    [HttpGet("my")]
    public async Task<IActionResult> GetMine()
    {
        var appUser = await _userManager.GetUserAsync(User);
        if (appUser?.EmployeeId == null)
            return Unauthorized(new { message = "No linked employee record." });

        var requests = await _svc.GetMyRequestsAsync(appUser.EmployeeId.Value);
        return Ok(requests.Select(r => new
        {
            r.Id,
            r.FieldName,
            r.FieldLabel,
            r.OldValue,
            r.NewValue,
            r.Reason,
            r.Tier,
            Status = r.Status.ToString(),
            r.CreatedAt,
            r.ReviewedAt,
            r.RejectionReason,
            r.AppliedAt
        }));
    }

    // ── HR endpoints ──────────────────────────────────────────────────────────

    /// <summary>
    /// Get all pending Tier 2 requests (HR queue).
    /// GET /api/profile-changes/pending
    /// </summary>
    [HttpGet("pending")]
    [Authorize(Roles = RoleSets.HRStaff)]
    public async Task<IActionResult> GetPending()
    {
        var requests = await _svc.GetPendingRequestsAsync();
        return Ok(requests.Select(r => new
        {
            r.Id,
            EmployeeName = r.Employee != null ? $"{r.Employee.FirstName} {r.Employee.LastName}" : "—",
            r.EmployeeId,
            r.FieldName,
            r.FieldLabel,
            r.OldValue,
            r.NewValue,
            r.Reason,
            r.Tier,
            Status = r.Status.ToString(),
            r.CreatedAt
        }));
    }

    /// <summary>
    /// Get all requests (history) optionally filtered by status.
    /// GET /api/profile-changes/all?status=Pending
    /// </summary>
    [HttpGet("all")]
    [Authorize(Roles = RoleSets.HRStaff)]
    public async Task<IActionResult> GetAll([FromQuery] string? status)
    {
        ChangeRequestStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<ChangeRequestStatus>(status, out var parsed))
            statusFilter = parsed;

        var requests = await _svc.GetAllRequestsAsync(statusFilter);
        return Ok(requests.Select(r => new
        {
            r.Id,
            EmployeeName = r.Employee != null ? $"{r.Employee.FirstName} {r.Employee.LastName}" : "—",
            r.EmployeeId,
            r.FieldName,
            r.FieldLabel,
            r.OldValue,
            r.NewValue,
            r.Reason,
            r.Tier,
            Status = r.Status.ToString(),
            r.CreatedAt,
            ReviewedByName = r.ReviewedBy != null ? $"{r.ReviewedBy.FirstName} {r.ReviewedBy.LastName}" : null,
            r.ReviewedAt,
            r.RejectionReason,
            r.AppliedAt
        }));
    }

    /// <summary>
    /// HR approves a pending request.
    /// POST /api/profile-changes/{id}/approve
    /// </summary>
    [HttpPost("{id:int}/approve")]
    [Authorize(Roles = RoleSets.HRStaff)]
    public async Task<IActionResult> Approve(int id)
    {
        var reviewer = await _userManager.GetUserAsync(User);
        if (reviewer?.EmployeeId == null)
            return Unauthorized(new { message = "Reviewer has no linked employee record." });

        var request = await _svc.ApproveRequestAsync(id, reviewer.EmployeeId.Value);
        return Ok(new
        {
            request.Id,
            Status = request.Status.ToString(),
            request.AppliedAt,
            message = $"{request.FieldLabel} has been approved and applied."
        });
    }

    /// <summary>
    /// HR rejects a pending request.
    /// POST /api/profile-changes/{id}/reject
    /// </summary>
    [HttpPost("{id:int}/reject")]
    [Authorize(Roles = RoleSets.HRStaff)]
    public async Task<IActionResult> Reject(int id, [FromBody] RejectChangeDto dto)
    {
        var reviewer = await _userManager.GetUserAsync(User);
        if (reviewer?.EmployeeId == null)
            return Unauthorized(new { message = "Reviewer has no linked employee record." });

        var request = await _svc.RejectRequestAsync(id, reviewer.EmployeeId.Value, dto.Reason);
        return Ok(new
        {
            request.Id,
            Status = request.Status.ToString(),
            message = $"Change request for {request.FieldLabel} has been rejected."
        });
    }
}

// ── Request/Response DTOs ─────────────────────────────────────────────────────

public record SubmitChangeDto(string FieldName, string? NewValue, string? Reason);
public record RejectChangeDto(string? Reason);
