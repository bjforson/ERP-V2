using Microsoft.EntityFrameworkCore;
using NickHR.Core.DTOs.Recruitment;
using NickHR.Core.Entities.Core;
using NickHR.Core.Entities.Recruitment;
using NickHR.Core.Enums;
using NickHR.Core.Interfaces;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Recruitment;

public class RecruitmentService : IRecruitmentService
{
    private readonly NickHRDbContext _db;

    public RecruitmentService(NickHRDbContext db)
    {
        _db = db;
    }

    // ─── Requisitions ────────────────────────────────────────────────────────

    public async Task<JobRequisition> CreateRequisitionAsync(string title, int departmentId,
        int? designationId, int? gradeId, int numberOfPositions,
        decimal? salaryRangeMin, decimal? salaryRangeMax,
        string? description, string? requirements, int requestedById)
    {
        var requisition = new JobRequisition
        {
            Title = title,
            DepartmentId = departmentId,
            DesignationId = designationId,
            GradeId = gradeId,
            NumberOfPositions = numberOfPositions,
            SalaryRangeMin = salaryRangeMin,
            SalaryRangeMax = salaryRangeMax,
            Description = description,
            Requirements = requirements,
            RequestedById = requestedById,
            Status = JobRequisitionStatus.PendingApproval
        };

        _db.JobRequisitions.Add(requisition);
        await _db.SaveChangesAsync();
        return requisition;
    }

    public async Task<List<JobRequisition>> GetRequisitionsAsync(JobRequisitionStatus? status = null)
    {
        var query = _db.JobRequisitions
            .Include(r => r.Department)
            .Include(r => r.Designation)
            .Include(r => r.Grade)
            .Include(r => r.RequestedBy)
            .Include(r => r.ApprovedBy)
            .Include(r => r.Applications)
            .AsQueryable();

        if (status.HasValue)
            query = query.Where(r => r.Status == status.Value);

        return await query.OrderByDescending(r => r.CreatedAt).ToListAsync();
    }

    public async Task<JobRequisition?> GetRequisitionByIdAsync(int id)
    {
        return await _db.JobRequisitions
            .Include(r => r.Department)
            .Include(r => r.Designation)
            .Include(r => r.Grade)
            .Include(r => r.RequestedBy)
            .Include(r => r.ApprovedBy)
            .Include(r => r.Applications)
                .ThenInclude(a => a.Candidate)
            .FirstOrDefaultAsync(r => r.Id == id);
    }

    public async Task<JobRequisition> ApproveRequisitionAsync(int id, int approvedById)
    {
        var requisition = await _db.JobRequisitions.FindAsync(id)
            ?? throw new InvalidOperationException($"Requisition {id} not found.");

        if (requisition.Status != JobRequisitionStatus.PendingApproval)
            throw new InvalidOperationException("Only requisitions in PendingApproval status can be approved.");

        requisition.Status = JobRequisitionStatus.Approved;
        requisition.ApprovedById = approvedById;
        requisition.ApprovedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return requisition;
    }

    public async Task<JobRequisition> PublishRequisitionAsync(int id)
    {
        var requisition = await _db.JobRequisitions.FindAsync(id)
            ?? throw new InvalidOperationException($"Requisition {id} not found.");

        if (requisition.Status != JobRequisitionStatus.Approved)
            throw new InvalidOperationException("Only approved requisitions can be published.");

        requisition.Status = JobRequisitionStatus.Published;

        var posting = new JobPosting
        {
            JobRequisitionId = id,
            PostedOn = "Both",
            IsActive = true,
            PublishedAt = DateTime.UtcNow
        };
        _db.JobPostings.Add(posting);

        await _db.SaveChangesAsync();
        return requisition;
    }

    public async Task<JobRequisition> CloseRequisitionAsync(int id)
    {
        var requisition = await _db.JobRequisitions.FindAsync(id)
            ?? throw new InvalidOperationException($"Requisition {id} not found.");

        requisition.Status = JobRequisitionStatus.Closed;
        requisition.ClosingDate = DateTime.UtcNow;

        // Deactivate any active postings
        var postings = await _db.JobPostings
            .Where(p => p.JobRequisitionId == id && p.IsActive)
            .ToListAsync();
        foreach (var posting in postings)
        {
            posting.IsActive = false;
            posting.ExpiresAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        return requisition;
    }

    // ─── Candidates & Applications ────────────────────────────────────────────

    public async Task<Candidate> CreateCandidateAsync(string firstName, string lastName,
        string email, string? phone, int? referredByEmployeeId = null)
    {
        var existing = await _db.Candidates
            .FirstOrDefaultAsync(c => c.Email.ToLower() == email.ToLower());
        if (existing != null)
            throw new InvalidOperationException($"A candidate with email '{email}' already exists (Id: {existing.Id}).");

        var candidate = new Candidate
        {
            FirstName = firstName,
            LastName = lastName,
            Email = email,
            Phone = phone,
            ReferredByEmployeeId = referredByEmployeeId
        };

        _db.Candidates.Add(candidate);
        await _db.SaveChangesAsync();
        return candidate;
    }

    public async Task<Application> CreateApplicationAsync(int jobRequisitionId, int candidateId,
        string? coverLetterPath = null, string? cvPath = null)
    {
        var requisition = await _db.JobRequisitions.FindAsync(jobRequisitionId)
            ?? throw new InvalidOperationException($"Requisition {jobRequisitionId} not found.");

        if (requisition.Status != JobRequisitionStatus.Published)
            throw new InvalidOperationException("Applications can only be submitted for published requisitions.");

        var duplicate = await _db.Applications
            .FirstOrDefaultAsync(a => a.JobRequisitionId == jobRequisitionId && a.CandidateId == candidateId);
        if (duplicate != null)
            throw new InvalidOperationException("This candidate has already applied for this requisition.");

        var application = new Application
        {
            JobRequisitionId = jobRequisitionId,
            CandidateId = candidateId,
            ApplicationDate = DateTime.UtcNow,
            Stage = ApplicationStage.Applied,
            CoverLetterPath = coverLetterPath,
            CVPath = cvPath
        };

        _db.Applications.Add(application);
        await _db.SaveChangesAsync();
        return application;
    }

    public async Task<List<Application>> GetApplicationsAsync(int? jobRequisitionId = null)
    {
        var query = _db.Applications
            .Include(a => a.Candidate)
            .Include(a => a.JobRequisition)
                .ThenInclude(r => r.Department)
            .AsQueryable();

        if (jobRequisitionId.HasValue)
            query = query.Where(a => a.JobRequisitionId == jobRequisitionId.Value);

        return await query.OrderByDescending(a => a.ApplicationDate).ToListAsync();
    }

    public async Task<ApplicationPipelineDto> GetApplicationPipelineAsync(int jobRequisitionId)
    {
        var apps = await _db.Applications
            .Where(a => a.JobRequisitionId == jobRequisitionId)
            .ToListAsync();

        return new ApplicationPipelineDto
        {
            Applied    = apps.Count(a => a.Stage == ApplicationStage.Applied),
            Screening  = apps.Count(a => a.Stage == ApplicationStage.Screening),
            Interview  = apps.Count(a => a.Stage == ApplicationStage.Interview),
            Offer      = apps.Count(a => a.Stage == ApplicationStage.Offer),
            Hired      = apps.Count(a => a.Stage == ApplicationStage.Hired),
            Rejected   = apps.Count(a => a.Stage == ApplicationStage.Rejected)
        };
    }

    public async Task<Application> MoveApplicationStageAsync(int applicationId,
        ApplicationStage newStage, string? notes = null)
    {
        var application = await _db.Applications.FindAsync(applicationId)
            ?? throw new InvalidOperationException($"Application {applicationId} not found.");

        application.Stage = newStage;
        if (!string.IsNullOrWhiteSpace(notes))
            application.Notes = notes;

        await _db.SaveChangesAsync();
        return application;
    }

    // ─── Interviews ───────────────────────────────────────────────────────────

    public async Task<Interview> ScheduleInterviewAsync(int applicationId, int interviewerId,
        DateTime scheduledAt, string interviewType)
    {
        if (await _db.Applications.FindAsync(applicationId) == null)
            throw new InvalidOperationException($"Application {applicationId} not found.");

        var interview = new Interview
        {
            ApplicationId = applicationId,
            InterviewerId = interviewerId,
            ScheduledAt = scheduledAt,
            InterviewType = interviewType
        };

        _db.Interviews.Add(interview);
        await _db.SaveChangesAsync();
        return interview;
    }

    public async Task<Interview> RecordInterviewResultAsync(int interviewId, decimal? score,
        string? recommendation, string? notes)
    {
        var interview = await _db.Interviews.FindAsync(interviewId)
            ?? throw new InvalidOperationException($"Interview {interviewId} not found.");

        interview.CompletedAt = DateTime.UtcNow;
        interview.OverallScore = score;
        interview.Recommendation = recommendation;
        interview.Notes = notes;

        await _db.SaveChangesAsync();
        return interview;
    }

    public async Task<List<Interview>> GetInterviewsAsync(int? applicationId = null)
    {
        var query = _db.Interviews
            .Include(i => i.Application)
                .ThenInclude(a => a.Candidate)
            .Include(i => i.Interviewer)
            .AsQueryable();

        if (applicationId.HasValue)
            query = query.Where(i => i.ApplicationId == applicationId.Value);

        return await query.OrderByDescending(i => i.ScheduledAt).ToListAsync();
    }

    // ─── Offers ───────────────────────────────────────────────────────────────

    public async Task<OfferLetter> CreateOfferAsync(int applicationId, decimal offeredSalary,
        DateTime startDate, DateTime expiryDate)
    {
        if (await _db.Applications.FindAsync(applicationId) == null)
            throw new InvalidOperationException($"Application {applicationId} not found.");

        var offer = new OfferLetter
        {
            ApplicationId = applicationId,
            OfferedSalary = offeredSalary,
            StartDate = startDate,
            ExpiryDate = expiryDate,
            Status = "Draft"
        };

        _db.OfferLetters.Add(offer);
        await _db.SaveChangesAsync();
        return offer;
    }

    public async Task<OfferLetter> SendOfferAsync(int offerId)
    {
        var offer = await _db.OfferLetters.FindAsync(offerId)
            ?? throw new InvalidOperationException($"Offer {offerId} not found.");

        if (offer.Status != "Draft")
            throw new InvalidOperationException("Only draft offers can be sent.");

        offer.Status = "Sent";
        offer.SentAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return offer;
    }

    public async Task<OfferLetter> RespondToOfferAsync(int offerId, string response)
    {
        if (response != "Accepted" && response != "Rejected")
            throw new ArgumentException("Response must be 'Accepted' or 'Rejected'.");

        var offer = await _db.OfferLetters.FindAsync(offerId)
            ?? throw new InvalidOperationException($"Offer {offerId} not found.");

        if (offer.Status != "Sent")
            throw new InvalidOperationException("Only sent offers can be responded to.");

        offer.Status = response;
        offer.RespondedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return offer;
    }

    public async Task<List<OfferLetter>> GetOffersAsync(string? status = null)
    {
        var query = _db.OfferLetters
            .Include(o => o.Application)
                .ThenInclude(a => a.Candidate)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(o => o.Status == status);

        return await query.OrderByDescending(o => o.CreatedAt).ToListAsync();
    }

    // ─── Onboarding ───────────────────────────────────────────────────────────

    public async Task<int> InitiateOnboardingAsync(int applicationId)
    {
        var application = await _db.Applications
            .Include(a => a.Candidate)
            .Include(a => a.JobRequisition)
            .FirstOrDefaultAsync(a => a.Id == applicationId)
            ?? throw new InvalidOperationException($"Application {applicationId} not found.");

        if (application.Stage == ApplicationStage.Hired)
            throw new InvalidOperationException("Onboarding has already been initiated for this application.");

        // Move application to Hired
        application.Stage = ApplicationStage.Hired;

        var candidate = application.Candidate;
        var requisition = application.JobRequisition;

        // Generate a unique employee code
        var year = DateTime.UtcNow.Year;
        var count = await _db.Employees.CountAsync() + 1;
        var employeeCode = $"EMP-{year}-{count:D4}";

        // Ensure uniqueness
        while (await _db.Employees.AnyAsync(e => e.EmployeeCode == employeeCode))
        {
            count++;
            employeeCode = $"EMP-{year}-{count:D4}";
        }

        var employee = new Employee
        {
            FirstName = candidate.FirstName,
            LastName = candidate.LastName,
            PersonalEmail = candidate.Email,
            PrimaryPhone = candidate.Phone,
            EmployeeCode = employeeCode,
            HireDate = DateTime.UtcNow,
            DepartmentId = requisition.DepartmentId,
            DesignationId = requisition.DesignationId,
            GradeId = requisition.GradeId,
            EmploymentStatus = EmploymentStatus.Active,
            EmploymentType = EmploymentType.FullTime
        };

        _db.Employees.Add(employee);
        await _db.SaveChangesAsync();

        return employee.Id;
    }

    public Task<List<OnboardingTaskDto>> GetOnboardingChecklistAsync(int employeeId)
    {
        // Hardcoded onboarding task template
        var checklist = new List<OnboardingTaskDto>
        {
            new() { Order = 1, Task = "IT Setup – Laptop, email account, and system access provisioned", Category = "IT",      IsCompleted = false },
            new() { Order = 2, Task = "ID Card – Employee ID card issued",                               Category = "Admin",   IsCompleted = false },
            new() { Order = 3, Task = "Bank Details – Bank account information collected for payroll",   Category = "Payroll", IsCompleted = false },
            new() { Order = 4, Task = "Policy Acknowledgement – Employee handbook signed and returned",  Category = "HR",      IsCompleted = false },
            new() { Order = 5, Task = "Buddy Assignment – Buddy/mentor assigned for first 30 days",     Category = "HR",      IsCompleted = false },
            new() { Order = 6, Task = "Probation Objectives – 90-day probation goals set with manager", Category = "Manager", IsCompleted = false }
        };

        return Task.FromResult(checklist);
    }
}
