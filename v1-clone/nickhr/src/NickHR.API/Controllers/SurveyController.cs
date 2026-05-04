using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NickHR.Core.DTOs;
using NickHR.Core.Enums;
using NickHR.Core.Interfaces;
using NickHR.Services.Survey;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SurveyController : ControllerBase
{
    private readonly SurveyService _service;
    private readonly ICurrentUserService _currentUser;

    public SurveyController(SurveyService service, ICurrentUserService currentUser)
    {
        _service = service;
        _currentUser = currentUser;
    }

    /// <summary>Create a new survey (draft).</summary>
    [HttpPost]
    [Authorize(Roles = RoleSets.HRStaff)]
    public async Task<IActionResult> CreateSurvey([FromBody] CreateSurveyRequest request)
    {
        try
        {
            var survey = await _service.CreateSurveyAsync(
                request.Title, request.Description, request.IsAnonymous,
                request.StartDate, request.EndDate, request.CreatedById);
            return Ok(ApiResponse<object>.Ok(new { survey.Id, survey.Title }, "Survey created."));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Add a question to a survey.</summary>
    [HttpPost("{surveyId}/questions")]
    [Authorize(Roles = RoleSets.HRStaff)]
    public async Task<IActionResult> AddQuestion(int surveyId, [FromBody] AddQuestionRequest request)
    {
        try
        {
            var question = await _service.AddQuestionAsync(
                surveyId, request.QuestionText, request.QuestionType,
                request.Options, request.OrderIndex);
            return Ok(ApiResponse<object>.Ok(new { question.Id }, "Question added."));
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

    /// <summary>Activate a survey (changes status from Draft to Active).</summary>
    [HttpPost("{surveyId}/activate")]
    [Authorize(Roles = RoleSets.HRStaff)]
    public async Task<IActionResult> Activate(int surveyId)
    {
        try
        {
            await _service.ActivateSurveyAsync(surveyId);
            return Ok(ApiResponse<object>.Ok(null, "Survey activated."));
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

    /// <summary>Close an active survey.</summary>
    [HttpPost("{surveyId}/close")]
    [Authorize(Roles = RoleSets.HRStaff)]
    public async Task<IActionResult> Close(int surveyId)
    {
        try
        {
            await _service.CloseSurveyAsync(surveyId);
            return Ok(ApiResponse<object>.Ok(null, "Survey closed."));
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

    /// <summary>Get all currently active surveys.</summary>
    [HttpGet("active")]
    public async Task<IActionResult> GetActiveSurveys()
    {
        var surveys = await _service.GetActiveSurveysAsync();
        return Ok(ApiResponse<object>.Ok(surveys));
    }

    /// <summary>Get a survey with its questions.</summary>
    [HttpGet("{surveyId}")]
    public async Task<IActionResult> GetSurvey(int surveyId)
    {
        var survey = await _service.GetSurveyWithQuestionsAsync(surveyId);
        if (survey == null) return NotFound(ApiResponse<object>.Fail("Survey not found."));
        return Ok(ApiResponse<object>.Ok(survey));
    }

    /// <summary>Submit a response to a survey.</summary>
    [HttpPost("{surveyId}/respond")]
    public async Task<IActionResult> Respond(int surveyId, [FromBody] SurveyResponseRequest request)
    {
        try
        {
            var answers = request.Answers
                .Select(a => (a.QuestionId, (string?)a.AnswerText, (int?)a.Rating))
                .ToList();

            var response = await _service.SubmitResponseAsync(surveyId, request.EmployeeId, answers);
            return Ok(ApiResponse<object>.Ok(new { response.Id }, "Response submitted successfully."));
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

    /// <summary>Get aggregated results for a survey (HR/Admin only).</summary>
    [HttpGet("{surveyId}/results")]
    [Authorize(Roles = RoleSets.HRStaff)]
    public async Task<IActionResult> GetResults(int surveyId)
    {
        try
        {
            var results = await _service.GetSurveyResultsAsync(surveyId);
            return Ok(ApiResponse<object>.Ok(results));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Check if an employee has already responded to a survey.</summary>
    [HttpGet("{surveyId}/has-responded/{employeeId}")]
    public async Task<IActionResult> HasResponded(int surveyId, int employeeId)
    {
        var result = await _service.HasRespondedAsync(surveyId, employeeId);
        return Ok(ApiResponse<object>.Ok(new { HasResponded = result }));
    }
}

public class CreateSurveyRequest
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsAnonymous { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public int CreatedById { get; set; }
}

public class AddQuestionRequest
{
    public string QuestionText { get; set; } = string.Empty;
    public SurveyQuestionType QuestionType { get; set; }
    public string? Options { get; set; }
    public int OrderIndex { get; set; }
}

public class SurveyResponseRequest
{
    public int? EmployeeId { get; set; }
    public List<AnswerInput> Answers { get; set; } = new();
}

public class AnswerInput
{
    public int QuestionId { get; set; }
    public string? AnswerText { get; set; }
    public int? Rating { get; set; }
}
