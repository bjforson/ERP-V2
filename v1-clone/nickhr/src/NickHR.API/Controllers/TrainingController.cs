using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NickHR.Core.DTOs;
using NickHR.Services.Training;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/training")]
[Authorize(Roles = RoleSets.HRStaffOrDeptManager)]
public class TrainingController : ControllerBase
{
    private readonly ITrainingService _training;

    public TrainingController(ITrainingService training)
    {
        _training = training;
    }

    // ─── Programs ────────────────────────────────────────────────────────────

    /// <summary>Create a new training program.</summary>
    [HttpPost("programs")]
    public async Task<IActionResult> CreateProgram([FromBody] CreateTrainingProgramRequest req)
    {
        try
        {
            var program = await _training.CreateProgramAsync(
                req.Title, req.Description, req.Provider, req.Location,
                req.StartDate, req.EndDate, req.MaxParticipants,
                req.Cost, req.TrainingType, req.DepartmentId);

            return Ok(ApiResponse<object>.Ok(new
            {
                program.Id,
                program.Title,
                program.Provider,
                program.TrainingType,
                program.StartDate,
                program.MaxParticipants
            }, "Training program created."));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>List training programs, optionally filter by active status.</summary>
    [HttpGet("programs")]
    public async Task<IActionResult> GetPrograms([FromQuery] bool? activeOnly = null)
    {
        var programs = await _training.GetProgramsAsync(activeOnly);
        var data = programs.Select(p => new
        {
            p.Id,
            p.Title,
            p.Provider,
            p.Location,
            p.StartDate,
            p.EndDate,
            p.MaxParticipants,
            p.Cost,
            p.TrainingType,
            Department = p.Department?.Name,
            p.IsActive,
            EnrolledCount = p.TrainingAttendances?.Count ?? 0
        });
        return Ok(ApiResponse<object>.Ok(data));
    }

    /// <summary>Get a training program by ID including attendees.</summary>
    [HttpGet("programs/{id}")]
    public async Task<IActionResult> GetProgram(int id)
    {
        var program = await _training.GetProgramByIdAsync(id);
        if (program is null)
            return NotFound(ApiResponse<object>.Fail($"Training program {id} not found."));

        return Ok(ApiResponse<object>.Ok(new
        {
            program.Id,
            program.Title,
            program.Description,
            program.Provider,
            program.Location,
            program.StartDate,
            program.EndDate,
            program.MaxParticipants,
            program.Cost,
            program.TrainingType,
            Department = program.Department?.Name,
            program.IsActive,
            Attendees = program.TrainingAttendances.Select(a => new
            {
                a.Id,
                a.EmployeeId,
                Employee = a.Employee is not null ? $"{a.Employee.FirstName} {a.Employee.LastName}" : null,
                a.Status,
                a.Score,
                a.CertificationName,
                a.CertificationExpiryDate
            })
        }));
    }

    // ─── Enrollment & Attendance ─────────────────────────────────────────────

    /// <summary>Enrol an employee into a training program.</summary>
    [HttpPost("programs/{programId}/enroll")]
    public async Task<IActionResult> EnrollEmployee(int programId, [FromBody] EnrollEmployeeRequest req)
    {
        try
        {
            var attendance = await _training.EnrollEmployeeAsync(programId, req.EmployeeId);
            return Ok(ApiResponse<object>.Ok(new
            {
                attendance.Id,
                attendance.TrainingProgramId,
                attendance.EmployeeId,
                attendance.Status
            }, "Employee enrolled successfully."));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Update attendance record (status, certification, score).</summary>
    [HttpPut("attendance/{attendanceId}")]
    public async Task<IActionResult> UpdateAttendance(int attendanceId, [FromBody] UpdateAttendanceRequest req)
    {
        try
        {
            var attendance = await _training.UpdateAttendanceAsync(
                attendanceId, req.Status, req.CertificationName,
                req.CertificationExpiryDate, req.Score);

            return Ok(ApiResponse<object>.Ok(new
            {
                attendance.Id,
                attendance.Status,
                attendance.CertificationName,
                attendance.CertificationExpiryDate,
                attendance.Score
            }, "Attendance updated."));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Get training history for an employee.</summary>
    [HttpGet("employees/{employeeId}/history")]
    public async Task<IActionResult> GetEmployeeTrainingHistory(int employeeId)
    {
        var history = await _training.GetEmployeeTrainingHistoryAsync(employeeId);
        var data = history.Select(a => new
        {
            a.Id,
            a.TrainingProgramId,
            Program = a.TrainingProgram?.Title,
            Provider = a.TrainingProgram?.Provider,
            StartDate = a.TrainingProgram?.StartDate,
            EndDate = a.TrainingProgram?.EndDate,
            a.Status,
            a.Score,
            a.CertificationName,
            a.CertificationExpiryDate
        });
        return Ok(ApiResponse<object>.Ok(data));
    }

    // ─── Skills ──────────────────────────────────────────────────────────────

    /// <summary>Get all active skills.</summary>
    [HttpGet("skills")]
    public async Task<IActionResult> GetSkills()
    {
        var skills = await _training.GetSkillsAsync();
        var data = skills.Select(s => new
        {
            s.Id,
            s.Name,
            s.Category,
            s.Description
        });
        return Ok(ApiResponse<object>.Ok(data));
    }

    /// <summary>Add or update a skill for an employee.</summary>
    [HttpPost("employees/{employeeId}/skills")]
    public async Task<IActionResult> AddEmployeeSkill(int employeeId, [FromBody] AddSkillRequest req)
    {
        try
        {
            var employeeSkill = await _training.AddEmployeeSkillAsync(
                employeeId, req.SkillId, req.ProficiencyLevel);

            return Ok(ApiResponse<object>.Ok(new
            {
                employeeSkill.Id,
                employeeSkill.EmployeeId,
                employeeSkill.SkillId,
                employeeSkill.ProficiencyLevel
            }, "Employee skill saved."));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Get skills for an employee.</summary>
    [HttpGet("employees/{employeeId}/skills")]
    public async Task<IActionResult> GetEmployeeSkills(int employeeId)
    {
        var skills = await _training.GetEmployeeSkillsAsync(employeeId);
        var data = skills.Select(es => new
        {
            es.Id,
            es.SkillId,
            SkillName = es.Skill?.Name,
            Category = es.Skill?.Category,
            es.ProficiencyLevel,
            es.CertifiedDate,
            es.Notes
        });
        return Ok(ApiResponse<object>.Ok(data));
    }

    /// <summary>Get skill gap report, optionally scoped to a department.</summary>
    [HttpGet("skills/gap-report")]
    public async Task<IActionResult> GetSkillGapReport([FromQuery] int? departmentId = null)
    {
        var report = await _training.GetSkillGapReportAsync(departmentId);
        return Ok(ApiResponse<object>.Ok(report));
    }
}

// ─── Request DTOs ─────────────────────────────────────────────────────────────

public record CreateTrainingProgramRequest(
    string Title,
    string? Description,
    string? Provider,
    string? Location,
    DateTime? StartDate,
    DateTime? EndDate,
    int MaxParticipants,
    decimal Cost,
    string TrainingType,
    int? DepartmentId);

public record EnrollEmployeeRequest(int EmployeeId);

public record UpdateAttendanceRequest(
    string Status,
    string? CertificationName,
    DateTime? CertificationExpiryDate,
    decimal? Score);

public record AddSkillRequest(int SkillId, int ProficiencyLevel);
