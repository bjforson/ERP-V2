using Ganss.Xss;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NickHR.Core.Interfaces;
using NickHR.Services.Announcement;
using NickHR.Services.Asset;
using NickHR.Services.Attendance;
using NickHR.Services.Auth;
using NickHR.Services.Common;
using NickHR.Services.Employee;
using NickHR.Services.Department;
using NickHR.Services.ExcuseDuty;
using NickHR.Services.Expense;
using NickHR.Services.Exit;
using NickHR.Services.Leave;
using NickHR.Services.Loans;
using NickHR.Services.Medical;
using NickHR.Services.Overtime;
using NickHR.Services.Payroll;
using NickHR.Services.Performance;
using NickHR.Services.Policy;
using NickHR.Services.Probation;
using NickHR.Services.Training;
using NickHR.Services.Discipline;
using NickHR.Services.Transfer;
using NickHR.Services.Recognition;
using NickHR.Services.Survey;
using NickHR.Services.Meeting;
using NickHR.Services.Compliance;
using NickHR.Services.Analytics;
using NickHR.Services.Letter;
using NickHR.Services.Communication;
using NickHR.Services.Project;
using NickHR.Services.Travel;
using NickHR.Services.Succession;
using NickHR.Services.Journal;
using NickHR.Services.Payroll.EFiling;
using NickHR.Services.OutOfStation;
using NickHR.Services.ProfileChange;

namespace NickHR.Services;

public static class ServiceExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services, IConfiguration configuration)
    {
        // XSS HARDENING (Wave 2J): central HtmlSanitizer used by LetterService and any
        // Razor component rendering user-influenced HTML. Default config strips
        // <script>, javascript: URLs, on* event handlers, and other XSS payloads
        // while preserving safe formatting (<p>, <strong>, <h1-6>, etc.). Singleton
        // is fine — HtmlSanitizer is thread-safe per its docs.
        services.AddSingleton<IHtmlSanitizer>(_ => new HtmlSanitizer());

        services.AddScoped<IAuthService, AuthService>();

        // Central Auth Client (for SSO with NSCIS)
        services.AddHttpClient<ICentralAuthClient, CentralAuthClient>((serviceProvider, client) =>
        {
            var config = serviceProvider.GetRequiredService<IConfiguration>();
            var baseUrl = config["CentralAuth:BaseUrl"] ?? "http://localhost:5205";
            client.BaseAddress = new Uri(baseUrl);
            client.Timeout = TimeSpan.FromSeconds(config.GetValue<int>("CentralAuth:TimeoutSeconds", 10));
        });

        // System Access orchestration (unified user provisioning across systems)
        services.AddScoped<ISystemAccessService, NickHR.Services.SystemAccess.SystemAccessService>();

        // NickComms Gateway client (single sending authority for email/SMS)
        services.AddHttpClient<INickCommsClient, NickHR.Services.Communication.NickCommsClient>(client =>
        {
            // BaseAddress and X-Api-Key are set per-call inside the client from IConfiguration
            // (with NICKCOMMS_BASE_URL / NICKCOMMS_API_KEY_NICKHR env var fallbacks).
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddScoped<IEmployeeService, EmployeeService>();
        services.AddScoped<IDepartmentService, DepartmentService>();
        services.AddScoped<ILeaveService, LeaveService>();
        services.AddScoped<IAttendanceService, AttendanceService>();
        services.AddScoped<IPayrollProcessingService, PayrollProcessingService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IRecruitmentService, NickHR.Services.Recruitment.RecruitmentService>();
        services.AddScoped<IPerformanceService, PerformanceService>();
        services.AddScoped<ITrainingService, TrainingService>();
        services.AddScoped<IDisciplineService, DisciplineService>();
        services.AddScoped<IExitService, ExitService>();
        services.AddScoped<IEmailService, EmailService>();
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUserService, CurrentUserService>();

        // Existing feature services
        services.AddScoped<ILoanApplicationService, LoanApplicationService>();
        services.AddScoped<IMedicalClaimService, MedicalClaimService>();
        services.AddScoped<IExcuseDutyService, ExcuseDutyService>();

        // New feature services
        services.AddScoped<IPayslipEmailService, PayslipEmailService>();
        services.AddScoped<IOvertimeService, OvertimeService>();
        services.AddScoped<IProbationService, ProbationService>();
        services.AddScoped<IAnnouncementService, AnnouncementService>();
        services.AddScoped<ITransferService, TransferService>();
        services.AddScoped<IAssetService, AssetService>();

        // New features (Phase 6)
        services.AddScoped<IExpenseClaimService, ExpenseClaimService>();
        services.AddScoped<IPolicyDocumentService, PolicyDocumentService>();
        services.AddScoped<IAlertService, AlertService>();

        // Batch 1 features
        services.AddScoped<RecognitionService>();
        services.AddScoped<SurveyService>();
        services.AddScoped<MeetingService>();
        services.AddScoped<WellnessService>();
        services.AddScoped<MobileMoneyPaymentService>();

        // Batch 2: Ghana Compliance
        services.AddScoped<EFilingService>();
        services.AddScoped<ComplianceService>();
        services.AddScoped<TurnoverAnalyticsService>();

        // Batch 3: Operational Efficiency
        services.AddScoped<DelegationService>();
        services.AddScoped<LetterService>();

        // Batch 4: Communication
        services.AddScoped<ISmsService, SmsService>();
        services.AddScoped<IWhatsAppService, WhatsAppService>();
        services.AddScoped<QrAttendanceService>();

        // Batch 5: Advanced Modules
        services.AddScoped<ProjectService>();
        services.AddScoped<TravelService>();
        services.AddScoped<SuccessionService>();
        services.AddScoped<JournalService>();
        services.AddSingleton<LocalizationService>();

        // Out of Station Allowance
        services.AddScoped<OutOfStationService>();

        // Profile Change Approval System
        services.AddScoped<ProfileChangeService>();

        return services;
    }
}
