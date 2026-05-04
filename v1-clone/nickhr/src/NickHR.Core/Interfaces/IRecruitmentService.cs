using NickHR.Core.DTOs.Recruitment;
using NickHR.Core.Entities.Recruitment;
using NickHR.Core.Enums;

namespace NickHR.Core.Interfaces;

/// <summary>
/// Core interface for the recruitment and onboarding service.
/// Implemented by NickHR.Services.Recruitment.RecruitmentService.
/// </summary>
public interface IRecruitmentService
{
    // Requisitions
    Task<JobRequisition> CreateRequisitionAsync(string title, int departmentId, int? designationId, int? gradeId,
        int numberOfPositions, decimal? salaryRangeMin, decimal? salaryRangeMax,
        string? description, string? requirements, int requestedById);
    Task<List<JobRequisition>> GetRequisitionsAsync(JobRequisitionStatus? status = null);
    Task<JobRequisition?> GetRequisitionByIdAsync(int id);
    Task<JobRequisition> ApproveRequisitionAsync(int id, int approvedById);
    Task<JobRequisition> PublishRequisitionAsync(int id);
    Task<JobRequisition> CloseRequisitionAsync(int id);

    // Candidates & Applications
    Task<Candidate> CreateCandidateAsync(string firstName, string lastName, string email,
        string? phone, int? referredByEmployeeId = null);
    Task<Application> CreateApplicationAsync(int jobRequisitionId, int candidateId,
        string? coverLetterPath = null, string? cvPath = null);
    Task<List<Application>> GetApplicationsAsync(int? jobRequisitionId = null);
    Task<ApplicationPipelineDto> GetApplicationPipelineAsync(int jobRequisitionId);
    Task<Application> MoveApplicationStageAsync(int applicationId, ApplicationStage newStage, string? notes = null);

    // Interviews
    Task<Interview> ScheduleInterviewAsync(int applicationId, int interviewerId,
        DateTime scheduledAt, string interviewType);
    Task<Interview> RecordInterviewResultAsync(int interviewId, decimal? score,
        string? recommendation, string? notes);
    Task<List<Interview>> GetInterviewsAsync(int? applicationId = null);

    // Offers
    Task<OfferLetter> CreateOfferAsync(int applicationId, decimal offeredSalary,
        DateTime startDate, DateTime expiryDate);
    Task<OfferLetter> SendOfferAsync(int offerId);
    Task<OfferLetter> RespondToOfferAsync(int offerId, string response);
    Task<List<OfferLetter>> GetOffersAsync(string? status = null);

    // Onboarding
    Task<int> InitiateOnboardingAsync(int applicationId);
    Task<List<OnboardingTaskDto>> GetOnboardingChecklistAsync(int employeeId);
}

/// <summary>Represents a single onboarding task in the checklist template.</summary>
public class OnboardingTaskDto
{
    public int Order { get; set; }
    public string Task { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
}
