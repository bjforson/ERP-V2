using Microsoft.EntityFrameworkCore;
using NickHR.Core.Entities.System;
using NickHR.Core.Enums;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Survey;

public class SurveyService
{
    private readonly NickHRDbContext _db;

    public SurveyService(NickHRDbContext db)
    {
        _db = db;
    }

    public async Task<NickHR.Core.Entities.System.Survey> CreateSurveyAsync(
        string title, string? description, bool isAnonymous,
        DateTime startDate, DateTime endDate, int createdById)
    {
        var survey = new NickHR.Core.Entities.System.Survey
        {
            Title = title,
            Description = description,
            IsAnonymous = isAnonymous,
            StartDate = startDate,
            EndDate = endDate,
            CreatedById = createdById,
            Status = SurveyStatus.Draft
        };

        _db.Surveys.Add(survey);
        await _db.SaveChangesAsync();
        return survey;
    }

    public async Task<SurveyQuestion> AddQuestionAsync(
        int surveyId, string text, SurveyQuestionType type, string? options, int order)
    {
        var survey = await _db.Surveys.FindAsync(surveyId)
            ?? throw new KeyNotFoundException("Survey not found.");

        if (survey.Status != SurveyStatus.Draft)
            throw new InvalidOperationException("Cannot add questions to a non-draft survey.");

        var question = new SurveyQuestion
        {
            SurveyId = surveyId,
            QuestionText = text,
            QuestionType = type,
            Options = options,
            OrderIndex = order
        };

        _db.SurveyQuestions.Add(question);
        await _db.SaveChangesAsync();
        return question;
    }

    public async Task ActivateSurveyAsync(int surveyId)
    {
        var survey = await _db.Surveys.FindAsync(surveyId)
            ?? throw new KeyNotFoundException("Survey not found.");

        if (survey.Status != SurveyStatus.Draft)
            throw new InvalidOperationException("Only draft surveys can be activated.");

        survey.Status = SurveyStatus.Active;
        await _db.SaveChangesAsync();
    }

    public async Task CloseSurveyAsync(int surveyId)
    {
        var survey = await _db.Surveys.FindAsync(surveyId)
            ?? throw new KeyNotFoundException("Survey not found.");

        if (survey.Status != SurveyStatus.Active)
            throw new InvalidOperationException("Only active surveys can be closed.");

        survey.Status = SurveyStatus.Closed;
        await _db.SaveChangesAsync();
    }

    public async Task<List<object>> GetActiveSurveysAsync()
    {
        var now = DateTime.UtcNow;
        return await _db.Surveys
            .Where(s => s.Status == SurveyStatus.Active && s.StartDate <= now && s.EndDate >= now)
            .Select(s => (object)new
            {
                s.Id,
                s.Title,
                s.Description,
                s.IsAnonymous,
                s.StartDate,
                s.EndDate,
                QuestionCount = s.Questions.Count
            })
            .ToListAsync();
    }

    public async Task<object?> GetSurveyWithQuestionsAsync(int surveyId)
    {
        var survey = await _db.Surveys
            .Include(s => s.Questions.OrderBy(q => q.OrderIndex))
            .FirstOrDefaultAsync(s => s.Id == surveyId);

        if (survey == null) return null;

        return new
        {
            survey.Id,
            survey.Title,
            survey.Description,
            survey.IsAnonymous,
            survey.StartDate,
            survey.EndDate,
            survey.Status,
            Questions = survey.Questions.Select(q => new
            {
                q.Id,
                q.QuestionText,
                q.QuestionType,
                q.Options,
                q.OrderIndex
            }).ToList()
        };
    }

    public async Task<SurveyResponse> SubmitResponseAsync(
        int surveyId, int? employeeId,
        List<(int questionId, string? text, int? rating)> answers)
    {
        var survey = await _db.Surveys.FindAsync(surveyId)
            ?? throw new KeyNotFoundException("Survey not found.");

        if (survey.Status != SurveyStatus.Active)
            throw new InvalidOperationException("Survey is not active.");

        if (!survey.IsAnonymous && employeeId.HasValue)
        {
            var already = await HasRespondedAsync(surveyId, employeeId.Value);
            if (already) throw new InvalidOperationException("You have already responded to this survey.");
        }

        var response = new SurveyResponse
        {
            SurveyId = surveyId,
            EmployeeId = survey.IsAnonymous ? null : employeeId,
            SubmittedAt = DateTime.UtcNow
        };

        _db.SurveyResponses.Add(response);
        await _db.SaveChangesAsync();

        foreach (var (questionId, text, ratingVal) in answers)
        {
            _db.SurveyAnswers.Add(new SurveyAnswer
            {
                SurveyResponseId = response.Id,
                SurveyQuestionId = questionId,
                AnswerText = text,
                Rating = ratingVal
            });
        }

        await _db.SaveChangesAsync();
        return response;
    }

    public async Task<object> GetSurveyResultsAsync(int surveyId)
    {
        var survey = await _db.Surveys
            .Include(s => s.Questions)
            .Include(s => s.Responses)
                .ThenInclude(r => r.Answers)
            .FirstOrDefaultAsync(s => s.Id == surveyId)
            ?? throw new KeyNotFoundException("Survey not found.");

        var questionResults = survey.Questions.Select(q =>
        {
            var answers = survey.Responses
                .SelectMany(r => r.Answers)
                .Where(a => a.SurveyQuestionId == q.Id)
                .ToList();

            double? avgRating = answers.Any(a => a.Rating.HasValue)
                ? answers.Where(a => a.Rating.HasValue).Average(a => (double)a.Rating!.Value)
                : null;

            var textAnswers = answers
                .Where(a => a.AnswerText != null)
                .Select(a => a.AnswerText!)
                .ToList();

            return new
            {
                q.Id,
                q.QuestionText,
                q.QuestionType,
                AverageRating = avgRating,
                ResponseCount = answers.Count,
                TextResponses = textAnswers
            };
        }).ToList();

        return new
        {
            survey.Id,
            survey.Title,
            TotalResponses = survey.Responses.Count,
            Questions = questionResults
        };
    }

    public async Task<bool> HasRespondedAsync(int surveyId, int employeeId)
    {
        return await _db.SurveyResponses
            .AnyAsync(r => r.SurveyId == surveyId && r.EmployeeId == employeeId);
    }
}
