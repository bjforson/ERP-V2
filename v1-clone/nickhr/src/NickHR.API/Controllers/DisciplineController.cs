using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NickHR.Core.DTOs;
using NickHR.Core.Enums;
using NickHR.Core.Interfaces;
using NickHR.Services.Discipline;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/discipline")]
[Authorize(Roles = RoleSets.HRStaff)]
public class DisciplineController : ControllerBase
{
    private readonly IDisciplineService _discipline;
    private readonly IAuditService _audit;
    private readonly ICurrentUserService _currentUser;

    public DisciplineController(
        IDisciplineService discipline,
        IAuditService audit,
        ICurrentUserService currentUser)
    {
        _discipline = discipline;
        _audit = audit;
        _currentUser = currentUser;
    }

    private string? RemoteIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    // ─── Disciplinary Cases ──────────────────────────────────────────────────

    /// <summary>Open a new disciplinary case.</summary>
    [HttpPost("cases")]
    public async Task<IActionResult> CreateCase([FromBody] CreateCaseRequest req)
    {
        try
        {
            var disciplinaryCase = await _discipline.CreateCaseAsync(
                req.EmployeeId, req.IncidentDate, req.Description,
                req.Witnesses, req.Evidence);

            // HIPAA-adjacent action: opening a disciplinary record creates a permanent
            // entry tied to a named employee. Capture WHO did it for audit replay.
            await _audit.LogAsync(
                userId: _currentUser.UserId,
                action: "DisciplinaryCase.Create",
                entityType: nameof(NickHR.Core.Entities.Discipline.DisciplinaryCase),
                entityId: disciplinaryCase.Id.ToString(),
                oldValues: null,
                newValues: JsonSerializer.Serialize(new { req.EmployeeId, req.IncidentDate, disciplinaryCase.Status }),
                ipAddress: RemoteIp);

            return Ok(ApiResponse<object>.Ok(new
            {
                disciplinaryCase.Id,
                disciplinaryCase.EmployeeId,
                disciplinaryCase.IncidentDate,
                disciplinaryCase.Status
            }, "Disciplinary case opened."));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>List disciplinary cases, optionally filtered by status.</summary>
    [HttpGet("cases")]
    public async Task<IActionResult> GetCases([FromQuery] string? status = null)
    {
        var cases = await _discipline.GetCasesAsync(status);
        var data = cases.Select(c => new
        {
            c.Id,
            c.EmployeeId,
            Employee = c.Employee is not null ? $"{c.Employee.FirstName} {c.Employee.LastName}" : null,
            c.IncidentDate,
            c.Description,
            c.Status,
            c.Action,
            c.ActionDate,
            c.AppealFiled,
            WarningCount = c.Warnings?.Count ?? 0
        });
        return Ok(ApiResponse<object>.Ok(data));
    }

    /// <summary>Get a disciplinary case by ID including warnings.</summary>
    [HttpGet("cases/{id}")]
    public async Task<IActionResult> GetCaseById(int id)
    {
        var disciplinaryCase = await _discipline.GetCaseByIdAsync(id);
        if (disciplinaryCase is null)
            return NotFound(ApiResponse<object>.Fail($"Disciplinary case {id} not found."));

        return Ok(ApiResponse<object>.Ok(new
        {
            disciplinaryCase.Id,
            disciplinaryCase.EmployeeId,
            Employee = disciplinaryCase.Employee is not null
                ? $"{disciplinaryCase.Employee.FirstName} {disciplinaryCase.Employee.LastName}" : null,
            disciplinaryCase.IncidentDate,
            disciplinaryCase.Description,
            disciplinaryCase.Witnesses,
            disciplinaryCase.Evidence,
            disciplinaryCase.HearingDate,
            disciplinaryCase.HearingNotes,
            disciplinaryCase.PanelMembers,
            Action = disciplinaryCase.Action?.ToString(),
            disciplinaryCase.ActionDate,
            disciplinaryCase.AppealFiled,
            disciplinaryCase.AppealOutcome,
            disciplinaryCase.Status,
            Warnings = disciplinaryCase.Warnings.Select(w => new
            {
                w.Id,
                WarningType = w.WarningType.ToString(),
                w.IssueDate,
                w.ExpiryDate,
                w.Description,
                IssuedBy = w.IssuedBy is not null ? $"{w.IssuedBy.FirstName} {w.IssuedBy.LastName}" : null,
                w.AcknowledgedAt
            })
        }));
    }

    /// <summary>Update the status of a disciplinary case (e.g., schedule hearing).</summary>
    [HttpPut("cases/{id}/status")]
    public async Task<IActionResult> UpdateCaseStatus(int id, [FromBody] UpdateCaseStatusRequest req)
    {
        try
        {
            var updated = await _discipline.UpdateCaseStatusAsync(
                id, req.Status, req.HearingNotes, req.PanelMembers);

            return Ok(ApiResponse<object>.Ok(new
            {
                updated.Id,
                updated.Status,
                updated.HearingNotes,
                updated.PanelMembers
            }, "Case status updated."));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Record a disciplinary action (warning) against an employee.</summary>
    [HttpPost("cases/{caseId}/actions")]
    public async Task<IActionResult> RecordAction(int caseId, [FromBody] RecordActionRequest req)
    {
        try
        {
            // SECURITY: ignore the client-supplied IssuedById and use the authenticated
            // user instead so a Warning row can't be forged onto a different HR officer.
            // (See Wave 2H notes — DTO field retained only for backward DTO compat.)
            var issuedById = _currentUser.EmployeeId
                ?? throw new InvalidOperationException("No employee profile linked to current user.");

            var warning = await _discipline.RecordActionAsync(
                caseId, req.Action, issuedById, req.Description);

            await _audit.LogAsync(
                userId: _currentUser.UserId,
                action: "DisciplinaryCase.RecordAction",
                entityType: nameof(NickHR.Core.Entities.Discipline.Warning),
                entityId: warning.Id.ToString(),
                oldValues: null,
                newValues: JsonSerializer.Serialize(new { caseId, Action = req.Action.ToString(), IssuedById = issuedById }),
                ipAddress: RemoteIp);

            return Ok(ApiResponse<object>.Ok(new
            {
                warning.Id,
                WarningType = warning.WarningType.ToString(),
                warning.IssueDate,
                warning.Description
            }, "Disciplinary action recorded."));
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

    // ─── Grievances ──────────────────────────────────────────────────────────

    /// <summary>File a new grievance.</summary>
    [HttpPost("grievances")]
    public async Task<IActionResult> CreateGrievance([FromBody] CreateGrievanceRequest req)
    {
        try
        {
            // PRIVACY (Wave 2H): when IsAnonymous is true the grievance must NOT
            // store the linkable EmployeeId, otherwise "anonymous" is anonymous to
            // the UI but transparent to anyone reading the row. Schema is now
            // `int?` (Grievance.EmployeeId nullable) — service layer maps anonymous
            // grievances to a true NULL FK, no more sentinel zeros.
            int? employeeIdForRow = req.IsAnonymous ? null : req.EmployeeId;

            var grievance = await _discipline.CreateGrievanceAsync(
                employeeIdForRow, req.Subject, req.Description, req.IsAnonymous);

            await _audit.LogAsync(
                userId: _currentUser.UserId,
                action: req.IsAnonymous ? "Grievance.CreateAnonymous" : "Grievance.Create",
                entityType: nameof(NickHR.Core.Entities.Discipline.Grievance),
                entityId: grievance.Id.ToString(),
                oldValues: null,
                newValues: JsonSerializer.Serialize(new { grievance.Subject, grievance.IsAnonymous }),
                ipAddress: RemoteIp);

            return Ok(ApiResponse<object>.Ok(new
            {
                grievance.Id,
                grievance.Subject,
                grievance.IsAnonymous,
                grievance.FiledDate,
                grievance.Status
            }, "Grievance filed."));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>List grievances, optionally filtered by status.</summary>
    [HttpGet("grievances")]
    public async Task<IActionResult> GetGrievances([FromQuery] string? status = null)
    {
        var grievances = await _discipline.GetGrievancesAsync(status);
        var data = grievances.Select(g => new
        {
            g.Id,
            g.Subject,
            Employee = g.IsAnonymous ? "Anonymous" : (g.Employee is not null
                ? $"{g.Employee.FirstName} {g.Employee.LastName}" : null),
            g.IsAnonymous,
            g.FiledDate,
            AssignedTo = g.AssignedTo is not null
                ? $"{g.AssignedTo.FirstName} {g.AssignedTo.LastName}" : null,
            g.Status,
            g.ResolvedAt
        });
        return Ok(ApiResponse<object>.Ok(data));
    }

    /// <summary>Assign a grievance to an investigator.</summary>
    [HttpPut("grievances/{id}/assign")]
    public async Task<IActionResult> AssignGrievance(int id, [FromBody] AssignGrievanceRequest req)
    {
        try
        {
            var grievance = await _discipline.AssignGrievanceAsync(id, req.AssignedToId);
            return Ok(ApiResponse<object>.Ok(new
            {
                grievance.Id,
                grievance.AssignedToId,
                grievance.Status
            }, "Grievance assigned."));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Resolve a grievance with a resolution note.</summary>
    [HttpPut("grievances/{id}/resolve")]
    public async Task<IActionResult> ResolveGrievance(int id, [FromBody] ResolveGrievanceRequest req)
    {
        try
        {
            var grievance = await _discipline.ResolveGrievanceAsync(id, req.Resolution);
            return Ok(ApiResponse<object>.Ok(new
            {
                grievance.Id,
                grievance.Status,
                grievance.Resolution,
                grievance.ResolvedAt
            }, "Grievance resolved."));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail(ex.Message));
        }
    }
}

// ─── Request DTOs ─────────────────────────────────────────────────────────────

public record CreateCaseRequest(
    int EmployeeId,
    DateTime IncidentDate,
    string Description,
    string? Witnesses,
    string? Evidence);

public record UpdateCaseStatusRequest(string Status, string? HearingNotes, string? PanelMembers);

public record RecordActionRequest(DisciplinaryAction Action, int IssuedById, string Description);

public record CreateGrievanceRequest(
    int EmployeeId,
    string Subject,
    string Description,
    bool IsAnonymous);

public record AssignGrievanceRequest(int AssignedToId);

public record ResolveGrievanceRequest(string Resolution);
