using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NickHR.Core.DTOs;
using NickHR.Core.Enums;
using NickHR.Core.Interfaces;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/recruitment")]
[Authorize(Roles = RoleSets.HRStaff)]
public class RecruitmentController : ControllerBase
{
    private readonly IRecruitmentService _recruitment;
    private readonly ICurrentUserService _currentUser;

    public RecruitmentController(IRecruitmentService recruitment, ICurrentUserService currentUser)
    {
        _recruitment = recruitment;
        _currentUser = currentUser;
    }

    // ─── Requisitions ────────────────────────────────────────────────────────

    /// <summary>Create a new job requisition.</summary>
    [HttpPost("requisitions")]
    public async Task<IActionResult> CreateRequisition([FromBody] CreateRequisitionRequest req)
    {
        try
        {
            var requestedById = _currentUser.EmployeeId
                ?? throw new InvalidOperationException("Current user has no linked employee record.");

            var result = await _recruitment.CreateRequisitionAsync(
                req.Title, req.DepartmentId, req.DesignationId, req.GradeId,
                req.NumberOfPositions, req.SalaryRangeMin, req.SalaryRangeMax,
                req.Description, req.Requirements, requestedById);

            return Ok(ApiResponse<object>.Ok(new { result.Id, result.Title, result.Status }, "Requisition created."));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>List requisitions, optionally filtered by status.</summary>
    [HttpGet("requisitions")]
    public async Task<IActionResult> GetRequisitions([FromQuery] JobRequisitionStatus? status = null)
    {
        var list = await _recruitment.GetRequisitionsAsync(status);
        var data = list.Select(r => new
        {
            r.Id,
            r.Title,
            Department = r.Department?.Name,
            Designation = r.Designation?.Title,
            Grade = r.Grade?.Name,
            r.NumberOfPositions,
            ApplicationCount = r.Applications?.Count ?? 0,
            r.Status,
            RequestedBy = r.RequestedBy != null ? $"{r.RequestedBy.FirstName} {r.RequestedBy.LastName}" : null,
            r.CreatedAt,
            r.ApprovedAt
        });
        return Ok(ApiResponse<object>.Ok(data));
    }

    /// <summary>Get a single requisition with full detail.</summary>
    [HttpGet("requisitions/{id:int}")]
    public async Task<IActionResult> GetRequisition(int id)
    {
        var r = await _recruitment.GetRequisitionByIdAsync(id);
        if (r == null) return NotFound(ApiResponse<object>.Fail("Requisition not found."));

        return Ok(ApiResponse<object>.Ok(new
        {
            r.Id,
            r.Title,
            Department = r.Department?.Name,
            Designation = r.Designation?.Title,
            Grade = r.Grade?.Name,
            r.NumberOfPositions,
            r.SalaryRangeMin,
            r.SalaryRangeMax,
            r.Description,
            r.Requirements,
            r.Status,
            RequestedBy = r.RequestedBy != null ? $"{r.RequestedBy.FirstName} {r.RequestedBy.LastName}" : null,
            ApprovedBy = r.ApprovedBy != null ? $"{r.ApprovedBy.FirstName} {r.ApprovedBy.LastName}" : null,
            r.ApprovedAt,
            r.ClosingDate,
            r.CreatedAt,
            ApplicationCount = r.Applications?.Count ?? 0
        }));
    }

    /// <summary>Approve a requisition.</summary>
    [HttpPost("requisitions/{id:int}/approve")]
    public async Task<IActionResult> ApproveRequisition(int id)
    {
        try
        {
            var approvedById = _currentUser.EmployeeId
                ?? throw new InvalidOperationException("Current user has no linked employee record.");

            var result = await _recruitment.ApproveRequisitionAsync(id, approvedById);
            return Ok(ApiResponse<object>.Ok(new { result.Id, result.Status }, "Requisition approved."));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Publish an approved requisition (creates a job posting).</summary>
    [HttpPost("requisitions/{id:int}/publish")]
    public async Task<IActionResult> PublishRequisition(int id)
    {
        try
        {
            var result = await _recruitment.PublishRequisitionAsync(id);
            return Ok(ApiResponse<object>.Ok(new { result.Id, result.Status }, "Requisition published."));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Close a requisition.</summary>
    [HttpPost("requisitions/{id:int}/close")]
    public async Task<IActionResult> CloseRequisition(int id)
    {
        try
        {
            var result = await _recruitment.CloseRequisitionAsync(id);
            return Ok(ApiResponse<object>.Ok(new { result.Id, result.Status }, "Requisition closed."));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    // ─── Candidates ───────────────────────────────────────────────────────────

    /// <summary>Create a candidate record.</summary>
    [HttpPost("candidates")]
    public async Task<IActionResult> CreateCandidate([FromBody] CreateCandidateRequest req)
    {
        try
        {
            var candidate = await _recruitment.CreateCandidateAsync(
                req.FirstName, req.LastName, req.Email, req.Phone, req.ReferredByEmployeeId);
            return Ok(ApiResponse<object>.Ok(new { candidate.Id, candidate.FirstName, candidate.LastName, candidate.Email }, "Candidate created."));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    // ─── Applications ─────────────────────────────────────────────────────────

    /// <summary>Submit an application for a requisition.</summary>
    [HttpPost("applications")]
    public async Task<IActionResult> CreateApplication([FromBody] CreateApplicationRequest req)
    {
        try
        {
            var app = await _recruitment.CreateApplicationAsync(
                req.JobRequisitionId, req.CandidateId, req.CoverLetterPath, req.CvPath);
            return Ok(ApiResponse<object>.Ok(new { app.Id, app.Stage, app.ApplicationDate }, "Application submitted."));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>List applications, optionally filtered by requisition.</summary>
    [HttpGet("applications")]
    public async Task<IActionResult> GetApplications([FromQuery] int? requisitionId = null)
    {
        var list = await _recruitment.GetApplicationsAsync(requisitionId);
        var data = list.Select(a => new
        {
            a.Id,
            Candidate = a.Candidate != null ? new
            {
                a.Candidate.Id,
                FullName = $"{a.Candidate.FirstName} {a.Candidate.LastName}",
                a.Candidate.Email,
                a.Candidate.Phone
            } : null,
            Requisition = a.JobRequisition != null ? new { a.JobRequisition.Id, a.JobRequisition.Title } : null,
            a.Stage,
            a.ApplicationDate,
            a.Notes
        });
        return Ok(ApiResponse<object>.Ok(data));
    }

    /// <summary>Get pipeline stage counts for a requisition.</summary>
    [HttpGet("applications/{id:int}/pipeline")]
    public async Task<IActionResult> GetApplicationPipeline(int id)
    {
        var pipeline = await _recruitment.GetApplicationPipelineAsync(id);
        return Ok(ApiResponse<object>.Ok(pipeline));
    }

    /// <summary>Move an application to a new stage.</summary>
    [HttpPost("applications/{id:int}/move")]
    public async Task<IActionResult> MoveApplicationStage(int id, [FromBody] MoveStageRequest req)
    {
        try
        {
            var app = await _recruitment.MoveApplicationStageAsync(id, req.Stage, req.Notes);
            return Ok(ApiResponse<object>.Ok(new { app.Id, app.Stage }, "Application stage updated."));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    // ─── Interviews ───────────────────────────────────────────────────────────

    /// <summary>Schedule an interview for an application.</summary>
    [HttpPost("interviews")]
    public async Task<IActionResult> ScheduleInterview([FromBody] ScheduleInterviewRequest req)
    {
        try
        {
            var interview = await _recruitment.ScheduleInterviewAsync(
                req.ApplicationId, req.InterviewerId, req.ScheduledAt, req.InterviewType);
            return Ok(ApiResponse<object>.Ok(new { interview.Id, interview.ScheduledAt, interview.InterviewType }, "Interview scheduled."));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Record the result of a completed interview.</summary>
    [HttpPost("interviews/{id:int}/result")]
    public async Task<IActionResult> RecordInterviewResult(int id, [FromBody] InterviewResultRequest req)
    {
        try
        {
            var interview = await _recruitment.RecordInterviewResultAsync(id, req.Score, req.Recommendation, req.Notes);
            return Ok(ApiResponse<object>.Ok(new { interview.Id, interview.OverallScore, interview.CompletedAt }, "Interview result recorded."));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>List interviews, optionally filtered by application.</summary>
    [HttpGet("interviews")]
    public async Task<IActionResult> GetInterviews([FromQuery] int? applicationId = null)
    {
        var list = await _recruitment.GetInterviewsAsync(applicationId);
        var data = list.Select(i => new
        {
            i.Id,
            i.ApplicationId,
            Candidate = i.Application?.Candidate != null
                ? $"{i.Application.Candidate.FirstName} {i.Application.Candidate.LastName}"
                : null,
            Interviewer = i.Interviewer != null
                ? $"{i.Interviewer.FirstName} {i.Interviewer.LastName}"
                : null,
            i.ScheduledAt,
            i.CompletedAt,
            i.InterviewType,
            i.OverallScore,
            i.Recommendation
        });
        return Ok(ApiResponse<object>.Ok(data));
    }

    // ─── Offers ───────────────────────────────────────────────────────────────

    /// <summary>Create an offer letter (starts as Draft).</summary>
    [HttpPost("offers")]
    public async Task<IActionResult> CreateOffer([FromBody] CreateOfferRequest req)
    {
        try
        {
            var offer = await _recruitment.CreateOfferAsync(
                req.ApplicationId, req.OfferedSalary, req.StartDate, req.ExpiryDate);
            return Ok(ApiResponse<object>.Ok(new { offer.Id, offer.Status }, "Offer letter created."));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Send a draft offer letter to the candidate.</summary>
    [HttpPost("offers/{id:int}/send")]
    public async Task<IActionResult> SendOffer(int id)
    {
        try
        {
            var offer = await _recruitment.SendOfferAsync(id);
            return Ok(ApiResponse<object>.Ok(new { offer.Id, offer.Status, offer.SentAt }, "Offer sent."));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Record the candidate's response to an offer (Accepted or Rejected).</summary>
    [HttpPost("offers/{id:int}/respond")]
    public async Task<IActionResult> RespondToOffer(int id, [FromBody] OfferResponseRequest req)
    {
        try
        {
            var offer = await _recruitment.RespondToOfferAsync(id, req.Response);
            return Ok(ApiResponse<object>.Ok(new { offer.Id, offer.Status, offer.RespondedAt }, "Offer response recorded."));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>List offer letters, optionally filtered by status.</summary>
    [HttpGet("offers")]
    public async Task<IActionResult> GetOffers([FromQuery] string? status = null)
    {
        var list = await _recruitment.GetOffersAsync(status);
        var data = list.Select(o => new
        {
            o.Id,
            o.ApplicationId,
            Candidate = o.Application?.Candidate != null
                ? $"{o.Application.Candidate.FirstName} {o.Application.Candidate.LastName}"
                : null,
            o.OfferedSalary,
            o.StartDate,
            o.ExpiryDate,
            o.Status,
            o.SentAt,
            o.RespondedAt
        });
        return Ok(ApiResponse<object>.Ok(data));
    }

    // ─── Onboarding ───────────────────────────────────────────────────────────

    /// <summary>Initiate onboarding – moves application to Hired and creates an Employee record.</summary>
    [HttpPost("onboarding/{applicationId:int}")]
    public async Task<IActionResult> InitiateOnboarding(int applicationId)
    {
        try
        {
            var employeeId = await _recruitment.InitiateOnboardingAsync(applicationId);
            return Ok(ApiResponse<object>.Ok(new { employeeId }, "Onboarding initiated. Employee record created."));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Get onboarding checklist for an employee.</summary>
    [HttpGet("onboarding/{employeeId:int}/checklist")]
    public async Task<IActionResult> GetOnboardingChecklist(int employeeId)
    {
        var checklist = await _recruitment.GetOnboardingChecklistAsync(employeeId);
        return Ok(ApiResponse<object>.Ok(checklist));
    }
}

// ─── Request Models ───────────────────────────────────────────────────────────

public class CreateRequisitionRequest
{
    public string Title { get; set; } = string.Empty;
    public int DepartmentId { get; set; }
    public int? DesignationId { get; set; }
    public int? GradeId { get; set; }
    public int NumberOfPositions { get; set; } = 1;
    public decimal? SalaryRangeMin { get; set; }
    public decimal? SalaryRangeMax { get; set; }
    public string? Description { get; set; }
    public string? Requirements { get; set; }
}

public class CreateCandidateRequest
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public int? ReferredByEmployeeId { get; set; }
}

public class CreateApplicationRequest
{
    public int JobRequisitionId { get; set; }
    public int CandidateId { get; set; }
    public string? CoverLetterPath { get; set; }
    public string? CvPath { get; set; }
}

public class MoveStageRequest
{
    public ApplicationStage Stage { get; set; }
    public string? Notes { get; set; }
}

public class ScheduleInterviewRequest
{
    public int ApplicationId { get; set; }
    public int InterviewerId { get; set; }
    public DateTime ScheduledAt { get; set; }
    /// <summary>Phone, InPerson, or Video</summary>
    public string InterviewType { get; set; } = "InPerson";
}

public class InterviewResultRequest
{
    public decimal? Score { get; set; }
    public string? Recommendation { get; set; }
    public string? Notes { get; set; }
}

public class CreateOfferRequest
{
    public int ApplicationId { get; set; }
    public decimal OfferedSalary { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime ExpiryDate { get; set; }
}

public class OfferResponseRequest
{
    /// <summary>Accepted or Rejected</summary>
    public string Response { get; set; } = string.Empty;
}
