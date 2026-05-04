using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using NickHR.Core.Entities;
using NickHR.Core.Entities.Core;
using NickHR.Core.Entities.Payroll;
using NickHR.Core.Entities.Recruitment;
using NickHR.Core.Entities.Leave;
using NickHR.Core.Entities.Performance;
using NickHR.Core.Entities.Training;
using NickHR.Core.Entities.Discipline;
using NickHR.Core.Entities.Exit;
using NickHR.Core.Entities.Medical;
using NickHR.Core.Entities.System;
using NickHR.Core.Interfaces;

namespace NickHR.Infrastructure.Data;

public class NickHRDbContext : IdentityDbContext<ApplicationUser, ApplicationRole, string>
{
    // Optional ICurrentUserService — null for design-time / migrations / non-HTTP
    // contexts (where there is no signed-in user). When present, UpdateTimestamps
    // also stamps CreatedBy / UpdatedBy on new + modified entities so audit
    // trails finally show who, not just when.
    private readonly ICurrentUserService? _currentUser;

    public NickHRDbContext(DbContextOptions<NickHRDbContext> options) : base(options) { }

    public NickHRDbContext(DbContextOptions<NickHRDbContext> options, ICurrentUserService currentUser)
        : base(options)
    {
        _currentUser = currentUser;
    }

    // Core
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Designation> Designations => Set<Designation>();
    public DbSet<Grade> Grades => Set<Grade>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<EmergencyContact> EmergencyContacts => Set<EmergencyContact>();
    public DbSet<Dependent> Dependents => Set<Dependent>();
    public DbSet<EmployeeDocument> EmployeeDocuments => Set<EmployeeDocument>();
    public DbSet<EmployeeQualification> EmployeeQualifications => Set<EmployeeQualification>();
    public DbSet<EmploymentHistoryRecord> EmploymentHistoryRecords => Set<EmploymentHistoryRecord>();
    public DbSet<Announcement> Announcements => Set<Announcement>();
    public DbSet<Beneficiary> Beneficiaries => Set<Beneficiary>();

    // Payroll
    public DbSet<SalaryComponent> SalaryComponents => Set<SalaryComponent>();
    public DbSet<EmployeeSalaryStructure> EmployeeSalaryStructures => Set<EmployeeSalaryStructure>();
    public DbSet<PayrollRun> PayrollRuns => Set<PayrollRun>();
    public DbSet<PayrollItem> PayrollItems => Set<PayrollItem>();
    public DbSet<PayrollItemDetail> PayrollItemDetails => Set<PayrollItemDetail>();
    public DbSet<Loan> Loans => Set<Loan>();
    public DbSet<LoanRepayment> LoanRepayments => Set<LoanRepayment>();

    // Recruitment
    public DbSet<JobRequisition> JobRequisitions => Set<JobRequisition>();
    public DbSet<JobPosting> JobPostings => Set<JobPosting>();
    public DbSet<Candidate> Candidates => Set<Candidate>();
    public DbSet<Application> Applications => Set<Application>();
    public DbSet<Interview> Interviews => Set<Interview>();
    public DbSet<OfferLetter> OfferLetters => Set<OfferLetter>();

    // Leave & Attendance
    public DbSet<LeavePolicy> LeavePolicies => Set<LeavePolicy>();
    public DbSet<LeaveBalance> LeaveBalances => Set<LeaveBalance>();
    public DbSet<LeaveRequest> LeaveRequests => Set<LeaveRequest>();
    public DbSet<Holiday> Holidays => Set<Holiday>();
    public DbSet<AttendanceRecord> AttendanceRecords => Set<AttendanceRecord>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<ShiftAssignment> ShiftAssignments => Set<ShiftAssignment>();

    // Performance
    public DbSet<AppraisalCycle> AppraisalCycles => Set<AppraisalCycle>();
    public DbSet<Goal> Goals => Set<Goal>();
    public DbSet<AppraisalForm> AppraisalForms => Set<AppraisalForm>();
    public DbSet<CompetencyFramework> CompetencyFrameworks => Set<CompetencyFramework>();
    public DbSet<Competency> Competencies => Set<Competency>();

    // Training
    public DbSet<TrainingProgram> TrainingPrograms => Set<TrainingProgram>();
    public DbSet<TrainingAttendance> TrainingAttendances => Set<TrainingAttendance>();
    public DbSet<Skill> Skills => Set<Skill>();
    public DbSet<EmployeeSkill> EmployeeSkills => Set<EmployeeSkill>();

    // Discipline
    public DbSet<DisciplinaryCase> DisciplinaryCases => Set<DisciplinaryCase>();
    public DbSet<Warning> Warnings => Set<Warning>();
    public DbSet<Grievance> Grievances => Set<Grievance>();

    // Exit
    public DbSet<Separation> Separations => Set<Separation>();
    public DbSet<ClearanceItem> ClearanceItems => Set<ClearanceItem>();
    public DbSet<ExitInterview> ExitInterviews => Set<ExitInterview>();
    public DbSet<FinalSettlement> FinalSettlements => Set<FinalSettlement>();

    // System
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<WorkflowStep> WorkflowSteps => Set<WorkflowStep>();
    public DbSet<Approval> Approvals => Set<Approval>();
    public DbSet<EmailTemplate> EmailTemplates => Set<EmailTemplate>();
    public DbSet<LoginAudit> LoginAudits => Set<LoginAudit>();
    public DbSet<UserSystemAccess> UserSystemAccesses => Set<UserSystemAccess>();

    // Loan Applications
    public DbSet<LoanApplication> LoanApplications => Set<LoanApplication>();

    // Medical
    public DbSet<MedicalClaim> MedicalClaims => Set<MedicalClaim>();
    public DbSet<MedicalBenefit> MedicalBenefits => Set<MedicalBenefit>();

    // Excuse Duty
    public DbSet<ExcuseDuty> ExcuseDuties => Set<ExcuseDuty>();
    public DbSet<CompanySetting> CompanySettings => Set<CompanySetting>();

    // Overtime
    public DbSet<OvertimeRequest> OvertimeRequests => Set<OvertimeRequest>();

    // Probation Reviews
    public DbSet<ProbationReview> ProbationReviews => Set<ProbationReview>();

    // Transfer / Promotion
    public DbSet<TransferPromotion> TransferPromotions => Set<TransferPromotion>();

    // Asset Management
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<AssetAssignment> AssetAssignments => Set<AssetAssignment>();

    // Expense Claims
    public DbSet<ExpenseClaim> ExpenseClaims => Set<ExpenseClaim>();

    // Policy Documents
    public DbSet<PolicyDocument> PolicyDocuments => Set<PolicyDocument>();
    public DbSet<PolicyAcknowledgement> PolicyAcknowledgements => Set<PolicyAcknowledgement>();

    // Recognition & Engagement
    public DbSet<Recognition> Recognitions => Set<Recognition>();
    public DbSet<EmployeeOfMonth> EmployeesOfMonth => Set<EmployeeOfMonth>();
    public DbSet<OneOnOneMeeting> OneOnOneMeetings => Set<OneOnOneMeeting>();

    // Surveys
    public DbSet<Survey> Surveys => Set<Survey>();
    public DbSet<SurveyQuestion> SurveyQuestions => Set<SurveyQuestion>();
    public DbSet<SurveyResponse> SurveyResponses => Set<SurveyResponse>();
    public DbSet<SurveyAnswer> SurveyAnswers => Set<SurveyAnswer>();

    // Batch 2-5 Features
    public DbSet<ComplianceDeadline> ComplianceDeadlines => Set<ComplianceDeadline>();
    public DbSet<ApprovalDelegation> ApprovalDelegations => Set<ApprovalDelegation>();
    public DbSet<LetterTemplate> LetterTemplates => Set<LetterTemplate>();
    public DbSet<GeneratedLetter> GeneratedLetters => Set<GeneratedLetter>();
    public DbSet<NickHR.Core.Entities.Core.Project> Projects => Set<NickHR.Core.Entities.Core.Project>();
    public DbSet<TimesheetEntry> TimesheetEntries => Set<TimesheetEntry>();
    public DbSet<TravelRequest> TravelRequests => Set<TravelRequest>();
    public DbSet<SuccessionPlan> SuccessionPlans => Set<SuccessionPlan>();
    public DbSet<SuccessionCandidate> SuccessionCandidates => Set<SuccessionCandidate>();
    public DbSet<AchievementEntry> AchievementEntries => Set<AchievementEntry>();

    // Out of Station
    public DbSet<OutOfStationRate> OutOfStationRates => Set<OutOfStationRate>();
    public DbSet<OutOfStationRequest> OutOfStationRequests => Set<OutOfStationRequest>();

    // Profile Change Requests
    public DbSet<ProfileChangeRequest> ProfileChangeRequests => Set<ProfileChangeRequest>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Rename Identity tables
        builder.Entity<ApplicationUser>().ToTable("Users");
        builder.Entity<ApplicationRole>().ToTable("Roles");
        builder.Entity<IdentityUserRole<string>>().ToTable("UserRoles");
        builder.Entity<IdentityUserClaim<string>>().ToTable("UserClaims");
        builder.Entity<IdentityUserLogin<string>>().ToTable("UserLogins");
        builder.Entity<IdentityRoleClaim<string>>().ToTable("RoleClaims");
        builder.Entity<IdentityUserToken<string>>().ToTable("UserTokens");

        // UserSystemAccess configuration
        builder.Entity<UserSystemAccess>(entity =>
        {
            entity.HasIndex(e => new { e.UserId, e.SystemName }).IsUnique();
            entity.HasIndex(e => e.SystemName);
        });

        // Global query filter for soft delete
        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            if (typeof(BaseEntity).IsAssignableFrom(entityType.ClrType))
            {
                var parameter = System.Linq.Expressions.Expression.Parameter(entityType.ClrType, "e");
                var property = System.Linq.Expressions.Expression.Property(parameter, nameof(BaseEntity.IsDeleted));
                var falseConstant = System.Linq.Expressions.Expression.Constant(false);
                var lambda = System.Linq.Expressions.Expression.Lambda(
                    System.Linq.Expressions.Expression.Equal(property, falseConstant), parameter);
                builder.Entity(entityType.ClrType).HasQueryFilter(lambda);
            }
        }

        // Employee self-referencing relationship
        builder.Entity<Employee>(entity =>
        {
            entity.HasIndex(e => e.EmployeeCode).IsUnique();
            entity.HasIndex(e => e.WorkEmail).IsUnique().HasFilter("\"WorkEmail\" IS NOT NULL");
            entity.HasIndex(e => e.GhanaCardNumber).IsUnique().HasFilter("\"GhanaCardNumber\" IS NOT NULL");
            entity.HasIndex(e => e.SSNITNumber).IsUnique().HasFilter("\"SSNITNumber\" IS NOT NULL");
            entity.HasIndex(e => e.TIN).IsUnique().HasFilter("\"TIN\" IS NOT NULL");

            // Wave 2L: FK-column indexes. These columns are used in nearly every
            // listing/dashboard query (`WHERE DepartmentId = X`, `JOIN Department`),
            // and Postgres won't auto-create them just because they're FKs. Without
            // these the planner does seq-scans on Employees once row count climbs.
            entity.HasIndex(e => e.DepartmentId);
            entity.HasIndex(e => e.DesignationId);
            entity.HasIndex(e => e.GradeId);
            entity.HasIndex(e => e.LocationId);
            entity.HasIndex(e => e.ReportingManagerId);

            entity.HasOne(e => e.ReportingManager)
                .WithMany(e => e.Subordinates)
                .HasForeignKey(e => e.ReportingManagerId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.Department)
                .WithMany(d => d.Employees)
                .HasForeignKey(e => e.DepartmentId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Designation)
                .WithMany(d => d.Employees)
                .HasForeignKey(e => e.DesignationId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Grade)
                .WithMany(g => g.Employees)
                .HasForeignKey(e => e.GradeId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Location)
                .WithMany(l => l.Employees)
                .HasForeignKey(e => e.LocationId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.Property(e => e.BasicSalary).HasPrecision(18, 2);
        });

        // Department self-referencing
        builder.Entity<Department>(entity =>
        {
            entity.HasIndex(d => d.Code).IsUnique();

            entity.HasOne(d => d.ParentDepartment)
                .WithMany(d => d.SubDepartments)
                .HasForeignKey(d => d.ParentDepartmentId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(d => d.HeadOfDepartment)
                .WithMany()
                .HasForeignKey(d => d.HeadOfDepartmentId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Grade
        builder.Entity<Grade>(entity =>
        {
            entity.Property(g => g.MinSalary).HasPrecision(18, 2);
            entity.Property(g => g.MidSalary).HasPrecision(18, 2);
            entity.Property(g => g.MaxSalary).HasPrecision(18, 2);
        });

        // Designation
        builder.Entity<Designation>(entity =>
        {
            entity.HasIndex(d => d.Code).IsUnique();
        });

        // Payroll precision
        builder.Entity<SalaryComponent>(entity =>
        {
            entity.HasIndex(s => s.Code).IsUnique();
            entity.Property(s => s.DefaultAmount).HasPrecision(18, 2);
            entity.Property(s => s.DefaultPercentage).HasPrecision(8, 4);
        });

        builder.Entity<EmployeeSalaryStructure>(entity =>
        {
            entity.Property(e => e.Amount).HasPrecision(18, 2);
        });

        builder.Entity<PayrollRun>(entity =>
        {
            entity.HasIndex(p => new { p.PayPeriodMonth, p.PayPeriodYear }).IsUnique();
            entity.Property(p => p.TotalGrossPay).HasPrecision(18, 2);
            entity.Property(p => p.TotalNetPay).HasPrecision(18, 2);
            entity.Property(p => p.TotalSSNITEmployee).HasPrecision(18, 2);
            entity.Property(p => p.TotalSSNITEmployer).HasPrecision(18, 2);
            entity.Property(p => p.TotalPAYE).HasPrecision(18, 2);
            entity.Property(p => p.TotalDeductions).HasPrecision(18, 2);
        });

        builder.Entity<PayrollItem>(entity =>
        {
            entity.HasIndex(p => new { p.PayrollRunId, p.EmployeeId }).IsUnique();
            // Wave 2L: leading-EmployeeId index for "all payslips for an employee"
            // queries — the (PayrollRunId, EmployeeId) unique index above can't
            // serve EmployeeId-alone lookups.
            entity.HasIndex(p => p.EmployeeId);
            entity.Property(p => p.BasicSalary).HasPrecision(18, 2);
            entity.Property(p => p.TotalAllowances).HasPrecision(18, 2);
            entity.Property(p => p.GrossPay).HasPrecision(18, 2);
            entity.Property(p => p.SSNITEmployee).HasPrecision(18, 2);
            entity.Property(p => p.SSNITEmployer).HasPrecision(18, 2);
            entity.Property(p => p.TaxableIncome).HasPrecision(18, 2);
            entity.Property(p => p.PAYE).HasPrecision(18, 2);
            entity.Property(p => p.TotalDeductions).HasPrecision(18, 2);
            entity.Property(p => p.NetPay).HasPrecision(18, 2);
            entity.Property(p => p.OvertimeHours).HasPrecision(8, 2);
            entity.Property(p => p.OvertimePay).HasPrecision(18, 2);
        });

        builder.Entity<PayrollItemDetail>(entity =>
        {
            entity.Property(p => p.Amount).HasPrecision(18, 2);
        });

        builder.Entity<Loan>(entity =>
        {
            entity.Property(l => l.Amount).HasPrecision(18, 2);
            entity.Property(l => l.InterestRate).HasPrecision(8, 4);
            entity.Property(l => l.TotalRepayable).HasPrecision(18, 2);
            entity.Property(l => l.MonthlyInstallment).HasPrecision(18, 2);
            entity.Property(l => l.BalanceRemaining).HasPrecision(18, 2);
        });

        builder.Entity<LoanRepayment>(entity =>
        {
            entity.Property(l => l.Amount).HasPrecision(18, 2);
            entity.Property(l => l.BalanceAfter).HasPrecision(18, 2);
        });

        // Recruitment
        builder.Entity<JobRequisition>(entity =>
        {
            entity.Property(j => j.SalaryRangeMin).HasPrecision(18, 2);
            entity.Property(j => j.SalaryRangeMax).HasPrecision(18, 2);

            entity.HasOne(j => j.RequestedBy)
                .WithMany()
                .HasForeignKey(j => j.RequestedById)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(j => j.ApprovedBy)
                .WithMany()
                .HasForeignKey(j => j.ApprovedById)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<Application>(entity =>
        {
            entity.HasIndex(a => new { a.JobRequisitionId, a.CandidateId }).IsUnique();
        });

        builder.Entity<Interview>(entity =>
        {
            entity.Property(i => i.OverallScore).HasPrecision(5, 2);

            entity.HasOne(i => i.Interviewer)
                .WithMany()
                .HasForeignKey(i => i.InterviewerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<OfferLetter>(entity =>
        {
            entity.Property(o => o.OfferedSalary).HasPrecision(18, 2);
        });

        // Leave
        builder.Entity<LeavePolicy>(entity =>
        {
            entity.Property(l => l.DefaultDays).HasPrecision(5, 1);
            entity.Property(l => l.MaxAccumulation).HasPrecision(5, 1);
            entity.Property(l => l.CarryForwardMax).HasPrecision(5, 1);
        });

        builder.Entity<LeaveBalance>(entity =>
        {
            entity.HasIndex(l => new { l.EmployeeId, l.LeavePolicyId, l.Year }).IsUnique();
            entity.Property(l => l.Entitled).HasPrecision(5, 1);
            entity.Property(l => l.Taken).HasPrecision(5, 1);
            entity.Property(l => l.Pending).HasPrecision(5, 1);
            entity.Property(l => l.CarriedForward).HasPrecision(5, 1);
        });

        builder.Entity<LeaveRequest>(entity =>
        {
            entity.Property(l => l.NumberOfDays).HasPrecision(5, 1);

            entity.HasOne(l => l.ApprovedBy)
                .WithMany()
                .HasForeignKey(l => l.ApprovedById)
                .OnDelete(DeleteBehavior.SetNull);

            // Wave 2L: leading-EmployeeId index for the common "my leave history"
            // and "leaves on date X" queries. Composite (EmployeeId, Status) makes
            // the dashboard "approved leaves today" filter index-only.
            entity.HasIndex(l => new { l.EmployeeId, l.Status });
        });

        builder.Entity<AttendanceRecord>(entity =>
        {
            entity.HasIndex(a => new { a.EmployeeId, a.Date }).IsUnique();
            entity.Property(a => a.WorkHours).HasPrecision(5, 2);
            entity.Property(a => a.OvertimeHours).HasPrecision(5, 2);
        });

        // Performance
        builder.Entity<Goal>(entity =>
        {
            entity.Property(g => g.Weight).HasPrecision(5, 2);
            entity.Property(g => g.ProgressPercent).HasPrecision(5, 2);
        });

        builder.Entity<AppraisalForm>(entity =>
        {
            entity.HasIndex(a => new { a.AppraisalCycleId, a.EmployeeId }).IsUnique();
            entity.Property(a => a.SelfRating).HasPrecision(5, 2);
            entity.Property(a => a.ManagerRating).HasPrecision(5, 2);
            entity.Property(a => a.FinalRating).HasPrecision(5, 2);

            entity.HasOne(a => a.Reviewer)
                .WithMany()
                .HasForeignKey(a => a.ReviewerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Competency>(entity =>
        {
            entity.Property(c => c.Weight).HasPrecision(5, 2);
        });

        // Training
        builder.Entity<TrainingProgram>(entity =>
        {
            entity.Property(t => t.Cost).HasPrecision(18, 2);
        });

        builder.Entity<TrainingAttendance>(entity =>
        {
            entity.HasIndex(t => new { t.TrainingProgramId, t.EmployeeId }).IsUnique();
            entity.Property(t => t.Score).HasPrecision(5, 2);
        });

        builder.Entity<EmployeeSkill>(entity =>
        {
            entity.HasIndex(e => new { e.EmployeeId, e.SkillId }).IsUnique();
        });

        // Discipline
        builder.Entity<Warning>(entity =>
        {
            entity.HasOne(w => w.IssuedBy)
                .WithMany()
                .HasForeignKey(w => w.IssuedById)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Grievance>(entity =>
        {
            entity.HasOne(g => g.AssignedTo)
                .WithMany()
                .HasForeignKey(g => g.AssignedToId)
                .OnDelete(DeleteBehavior.SetNull);

            // EmployeeId is nullable to support anonymous grievances. When the
            // filer is anonymous the row stores no linkable identifier.
            entity.HasOne(g => g.Employee)
                .WithMany()
                .HasForeignKey(g => g.EmployeeId)
                .OnDelete(DeleteBehavior.SetNull)
                .IsRequired(false);
        });

        // Exit
        builder.Entity<Separation>(entity =>
        {
            entity.HasOne(s => s.ApprovedBy)
                .WithMany()
                .HasForeignKey(s => s.ApprovedById)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<ClearanceItem>(entity =>
        {
            entity.HasOne(c => c.ClearedBy)
                .WithMany()
                .HasForeignKey(c => c.ClearedById)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<ExitInterview>(entity =>
        {
            entity.HasOne(e => e.Interviewer)
                .WithMany()
                .HasForeignKey(e => e.InterviewerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<FinalSettlement>(entity =>
        {
            entity.Property(f => f.LeaveEncashment).HasPrecision(18, 2);
            entity.Property(f => f.ProRatedBonus).HasPrecision(18, 2);
            entity.Property(f => f.GratuityAmount).HasPrecision(18, 2);
            entity.Property(f => f.LoanRecovery).HasPrecision(18, 2);
            entity.Property(f => f.OtherDeductions).HasPrecision(18, 2);
            entity.Property(f => f.TotalSettlement).HasPrecision(18, 2);
        });

        // System
        builder.Entity<AuditLog>(entity =>
        {
            entity.HasIndex(a => a.Timestamp);
            entity.HasIndex(a => a.UserId);
            entity.HasIndex(a => a.EntityType);
        });

        builder.Entity<CompanySetting>(entity =>
        {
            entity.HasIndex(c => c.Key).IsUnique();
        });

        builder.Entity<EmailTemplate>(entity =>
        {
            entity.HasIndex(e => e.Code).IsUnique();
        });

        builder.Entity<Approval>(entity =>
        {
            entity.HasOne(a => a.Approver)
                .WithMany()
                .HasForeignKey(a => a.ApproverId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(a => new { a.EntityType, a.EntityId });
        });

        builder.Entity<Notification>(entity =>
        {
            entity.HasOne(n => n.RecipientEmployee)
                .WithMany()
                .HasForeignKey(n => n.RecipientEmployeeId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(n => new { n.RecipientEmployeeId, n.IsRead });
        });

        // Loan Applications
        builder.Entity<LoanApplication>(entity =>
        {
            entity.Property(l => l.RequestedAmount).HasPrecision(18, 2);
            entity.Property(l => l.MonthlyInstallment).HasPrecision(18, 2);
            entity.Property(l => l.InterestRate).HasPrecision(8, 4);
            entity.Property(l => l.TotalRepayable).HasPrecision(18, 2);

            entity.HasOne(l => l.Employee)
                .WithMany()
                .HasForeignKey(l => l.EmployeeId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(l => l.GuarantorEmployee)
                .WithMany()
                .HasForeignKey(l => l.GuarantorEmployeeId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Medical Claims
        builder.Entity<MedicalClaim>(entity =>
        {
            entity.Property(m => m.ClaimAmount).HasPrecision(18, 2);
            entity.Property(m => m.ApprovedAmount).HasPrecision(18, 2);

            entity.HasOne(m => m.Employee)
                .WithMany()
                .HasForeignKey(m => m.EmployeeId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<MedicalBenefit>(entity =>
        {
            entity.Property(m => m.AnnualLimit).HasPrecision(18, 2);
        });

        // Excuse Duty
        builder.Entity<ExcuseDuty>(entity =>
        {
            entity.Property(e => e.DurationHours).HasPrecision(5, 2);
            entity.Property(e => e.ActualDurationHours).HasPrecision(5, 2);

            entity.HasOne(e => e.Employee)
                .WithMany()
                .HasForeignKey(e => e.EmployeeId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.ApprovedBy)
                .WithMany()
                .HasForeignKey(e => e.ApprovedById)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => new { e.EmployeeId, e.Date });
        });

        // Overtime
        builder.Entity<OvertimeRequest>(entity =>
        {
            entity.Property(o => o.PlannedHours).HasPrecision(5, 2);
            entity.Property(o => o.ActualHours).HasPrecision(5, 2);
            entity.Property(o => o.Rate).HasPrecision(4, 2);
            entity.Property(o => o.PayAmount).HasPrecision(18, 2);

            entity.HasOne(o => o.Employee)
                .WithMany()
                .HasForeignKey(o => o.EmployeeId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(o => o.ApprovedBy)
                .WithMany()
                .HasForeignKey(o => o.ApprovedById)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Transfer/Promotions
        builder.Entity<TransferPromotion>(entity =>
        {
            entity.Property(t => t.OldBasicSalary).HasPrecision(18, 2);
            entity.Property(t => t.NewBasicSalary).HasPrecision(18, 2);

            entity.HasOne(t => t.Employee)
                .WithMany()
                .HasForeignKey(t => t.EmployeeId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(t => t.ApprovedBy)
                .WithMany()
                .HasForeignKey(t => t.ApprovedById)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(t => t.FromDepartment)
                .WithMany()
                .HasForeignKey(t => t.FromDepartmentId).OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(t => t.ToDepartment)
                .WithMany()
                .HasForeignKey(t => t.ToDepartmentId).OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(t => t.FromDesignation)
                .WithMany()
                .HasForeignKey(t => t.FromDesignationId).OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(t => t.ToDesignation)
                .WithMany()
                .HasForeignKey(t => t.ToDesignationId).OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(t => t.FromGrade)
                .WithMany()
                .HasForeignKey(t => t.FromGradeId).OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(t => t.ToGrade)
                .WithMany()
                .HasForeignKey(t => t.ToGradeId).OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(t => t.FromLocation)
                .WithMany()
                .HasForeignKey(t => t.FromLocationId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(t => t.ToLocation)
                .WithMany()
                .HasForeignKey(t => t.ToLocationId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Assets
        builder.Entity<Asset>(entity =>
        {
            entity.Property(a => a.PurchasePrice).HasPrecision(18, 2);
            entity.HasIndex(a => a.AssetTag).IsUnique();

            entity.HasOne(a => a.AssignedToEmployee)
                .WithMany()
                .HasForeignKey(a => a.AssignedToEmployeeId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<AssetAssignment>(entity =>
        {
            entity.HasOne(a => a.Asset)
                .WithMany(a => a.Assignments)
                .HasForeignKey(a => a.AssetId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(a => a.Employee)
                .WithMany()
                .HasForeignKey(a => a.EmployeeId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(a => a.AssignedBy)
                .WithMany()
                .HasForeignKey(a => a.AssignedById)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Probation Reviews
        builder.Entity<ProbationReview>(entity =>
        {
            entity.HasOne(p => p.Employee)
                .WithMany()
                .HasForeignKey(p => p.EmployeeId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(p => p.ReviewedBy)
                .WithMany()
                .HasForeignKey(p => p.ReviewedById)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Expense Claims
        builder.Entity<ExpenseClaim>(entity =>
        {
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.ApprovedAmount).HasPrecision(18, 2);

            entity.HasOne(e => e.Employee)
                .WithMany()
                .HasForeignKey(e => e.EmployeeId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.ApprovedBy)
                .WithMany()
                .HasForeignKey(e => e.ApprovedById)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Policy Documents
        builder.Entity<PolicyDocument>(entity =>
        {
            entity.HasMany(p => p.Acknowledgements)
                .WithOne(a => a.PolicyDocument)
                .HasForeignKey(a => a.PolicyDocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PolicyAcknowledgement>(entity =>
        {
            entity.HasIndex(a => new { a.PolicyDocumentId, a.EmployeeId }).IsUnique();

            entity.HasOne(a => a.Employee)
                .WithMany()
                .HasForeignKey(a => a.EmployeeId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Recognition
        builder.Entity<Recognition>(entity =>
        {
            entity.HasOne(r => r.SenderEmployee)
                .WithMany()
                .HasForeignKey(r => r.SenderEmployeeId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(r => r.RecipientEmployee)
                .WithMany()
                .HasForeignKey(r => r.RecipientEmployeeId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(r => r.CreatedAt);
        });

        // Employee of Month
        builder.Entity<EmployeeOfMonth>(entity =>
        {
            entity.HasIndex(e => new { e.Month, e.Year, e.EmployeeId }).IsUnique();

            entity.HasOne(e => e.Employee)
                .WithMany()
                .HasForeignKey(e => e.EmployeeId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.NominatedBy)
                .WithMany()
                .HasForeignKey(e => e.NominatedById)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Surveys
        builder.Entity<Survey>(entity =>
        {
            entity.HasOne(s => s.CreatedBy)
                .WithMany()
                .HasForeignKey(s => s.CreatedById)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<SurveyQuestion>(entity =>
        {
            entity.HasOne(q => q.Survey)
                .WithMany(s => s.Questions)
                .HasForeignKey(q => q.SurveyId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<SurveyResponse>(entity =>
        {
            entity.HasOne(r => r.Survey)
                .WithMany(s => s.Responses)
                .HasForeignKey(r => r.SurveyId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(r => r.Employee)
                .WithMany()
                .HasForeignKey(r => r.EmployeeId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<SurveyAnswer>(entity =>
        {
            entity.HasOne(a => a.SurveyResponse)
                .WithMany(r => r.Answers)
                .HasForeignKey(a => a.SurveyResponseId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(a => a.SurveyQuestion)
                .WithMany()
                .HasForeignKey(a => a.SurveyQuestionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Compliance Deadlines
        builder.Entity<ComplianceDeadline>(entity =>
        {
            entity.HasOne(c => c.CompletedBy)
                .WithMany()
                .HasForeignKey(c => c.CompletedById)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Approval Delegations
        builder.Entity<ApprovalDelegation>(entity =>
        {
            entity.HasOne(d => d.Delegator)
                .WithMany()
                .HasForeignKey(d => d.DelegatorId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(d => d.Delegate)
                .WithMany()
                .HasForeignKey(d => d.DelegateId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Letter Templates
        builder.Entity<LetterTemplate>(entity =>
        {
            entity.HasIndex(l => l.Code).IsUnique();
        });

        // Generated Letters
        builder.Entity<GeneratedLetter>(entity =>
        {
            entity.HasOne(g => g.LetterTemplate)
                .WithMany()
                .HasForeignKey(g => g.LetterTemplateId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(g => g.Employee)
                .WithMany()
                .HasForeignKey(g => g.EmployeeId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(g => g.GeneratedBy)
                .WithMany()
                .HasForeignKey(g => g.GeneratedById)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Projects
        builder.Entity<NickHR.Core.Entities.Core.Project>(entity =>
        {
            entity.HasIndex(p => p.Code).IsUnique();
            entity.Property(p => p.Budget).HasPrecision(18, 2);

            entity.HasOne(p => p.Manager)
                .WithMany()
                .HasForeignKey(p => p.ManagerId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Timesheet Entries
        builder.Entity<TimesheetEntry>(entity =>
        {
            entity.Property(t => t.Hours).HasPrecision(5, 2);

            entity.HasOne(t => t.Employee)
                .WithMany()
                .HasForeignKey(t => t.EmployeeId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(t => t.Project)
                .WithMany(p => p.TimesheetEntries)
                .HasForeignKey(t => t.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Travel Requests
        builder.Entity<TravelRequest>(entity =>
        {
            entity.Property(t => t.EstimatedCost).HasPrecision(18, 2);
            entity.Property(t => t.AdvanceRequested).HasPrecision(18, 2);
            entity.Property(t => t.ActualCost).HasPrecision(18, 2);

            entity.HasOne(t => t.Employee)
                .WithMany()
                .HasForeignKey(t => t.EmployeeId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(t => t.ApprovedBy)
                .WithMany()
                .HasForeignKey(t => t.ApprovedById)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Succession Plans
        builder.Entity<SuccessionPlan>(entity =>
        {
            entity.HasOne(s => s.Designation)
                .WithMany()
                .HasForeignKey(s => s.DesignationId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(s => s.IncumbentEmployee)
                .WithMany()
                .HasForeignKey(s => s.IncumbentEmployeeId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Succession Candidates
        builder.Entity<SuccessionCandidate>(entity =>
        {
            entity.HasOne(c => c.SuccessionPlan)
                .WithMany(s => s.Candidates)
                .HasForeignKey(c => c.SuccessionPlanId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(c => c.CandidateEmployee)
                .WithMany()
                .HasForeignKey(c => c.CandidateEmployeeId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Achievement Entries
        builder.Entity<AchievementEntry>(entity =>
        {
            entity.HasOne(a => a.Employee)
                .WithMany()
                .HasForeignKey(a => a.EmployeeId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(a => a.LinkedGoal)
                .WithMany()
                .HasForeignKey(a => a.LinkedGoalId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // 1-on-1 Meetings
        builder.Entity<OneOnOneMeeting>(entity =>
        {
            entity.HasOne(m => m.Manager)
                .WithMany()
                .HasForeignKey(m => m.ManagerId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(m => m.Employee)
                .WithMany()
                .HasForeignKey(m => m.EmployeeId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(m => new { m.ManagerId, m.ScheduledDate });
        });

        // Out of Station Rates
        builder.Entity<OutOfStationRate>(entity =>
        {
            entity.Property(r => r.AccommodationRate).HasPrecision(18, 2);
            entity.Property(r => r.FeedingRate).HasPrecision(18, 2);
            entity.Property(r => r.TransportRoadRate).HasPrecision(18, 2);
            entity.Property(r => r.TransportAirRate).HasPrecision(18, 2);
            entity.Property(r => r.MiscellaneousRate).HasPrecision(18, 2);

            entity.HasOne(r => r.Grade)
                .WithMany()
                .HasForeignKey(r => r.GradeId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(r => new { r.GradeId, r.DestinationType, r.IsActive });
        });

        // Out of Station Requests
        builder.Entity<OutOfStationRequest>(entity =>
        {
            entity.Property(r => r.AccommodationTotal).HasPrecision(18, 2);
            entity.Property(r => r.FeedingTotal).HasPrecision(18, 2);
            entity.Property(r => r.TransportTotal).HasPrecision(18, 2);
            entity.Property(r => r.MiscellaneousTotal).HasPrecision(18, 2);
            entity.Property(r => r.TotalAllowance).HasPrecision(18, 2);
            entity.Property(r => r.AdvancePaid).HasPrecision(18, 2);
            entity.Property(r => r.ActualExpenses).HasPrecision(18, 2);
            entity.Property(r => r.SettlementAmount).HasPrecision(18, 2);

            entity.HasOne(r => r.Employee)
                .WithMany()
                .HasForeignKey(r => r.EmployeeId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(r => r.ApprovedBy)
                .WithMany()
                .HasForeignKey(r => r.ApprovedById)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(r => new { r.EmployeeId, r.Status });
        });

        // Profile Change Requests
        builder.Entity<ProfileChangeRequest>(entity =>
        {
            entity.HasOne(r => r.Employee)
                .WithMany()
                .HasForeignKey(r => r.EmployeeId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(r => r.ReviewedBy)
                .WithMany()
                .HasForeignKey(r => r.ReviewedById)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(r => new { r.EmployeeId, r.Status });
            entity.HasIndex(r => r.Status);
        });
    }

    public override int SaveChanges()
    {
        UpdateTimestamps();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        UpdateTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void UpdateTimestamps()
    {
        // Best-effort identity capture. _currentUser is null for design-time and
        // background workers; UserId may be empty if a code path saves before the
        // user is fully authenticated. Falling back to null leaves the column
        // untouched rather than blowing up the SaveChanges call.
        var actor = !string.IsNullOrWhiteSpace(_currentUser?.UserId) ? _currentUser!.UserId : null;
        var now = DateTime.UtcNow;

        var entries = ChangeTracker.Entries<BaseEntity>();
        foreach (var entry in entries)
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                if (actor != null && string.IsNullOrEmpty(entry.Entity.CreatedBy))
                {
                    entry.Entity.CreatedBy = actor;
                }
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
                if (actor != null)
                {
                    entry.Entity.UpdatedBy = actor;
                }
            }
        }
    }
}
