using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NickHR.Core.Entities.Core;
using NickHR.Core.Entities.Leave;
using NickHR.Core.Entities.Payroll;
using NickHR.Core.Entities.System;
using NickHR.Core.Enums;

namespace NickHR.Infrastructure.Data;

public static class SeedData
{
    public static async Task InitializeAsync(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<NickHRDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();

        await context.Database.MigrateAsync();

        await SeedRolesAsync(roleManager);
        await SeedAdminUserAsync(userManager);
        await SeedGradesAsync(context);
        await SeedLocationsAsync(context);
        await SeedSalaryComponentsAsync(context);
        await SeedLeavePoliciesAsync(context);
        await SeedHolidaysAsync(context);
        await SeedCompanySettingsAsync(context);
        await SeedEmailTemplatesAsync(context);
        await EnsureNewTemplatesAsync(context);
    }

    private static async Task SeedRolesAsync(RoleManager<ApplicationRole> roleManager)
    {
        var roles = new[]
        {
            new ApplicationRole { Name = "SuperAdmin", Description = "Full system access", IsSystemRole = true },
            new ApplicationRole { Name = "HRManager", Description = "HR department manager", IsSystemRole = true },
            new ApplicationRole { Name = "HROfficer", Description = "HR department officer", IsSystemRole = true },
            new ApplicationRole { Name = "PayrollAdmin", Description = "Payroll administration", IsSystemRole = true },
            new ApplicationRole { Name = "DepartmentManager", Description = "Department/line manager", IsSystemRole = true },
            new ApplicationRole { Name = "Employee", Description = "Regular employee self-service", IsSystemRole = true }
        };

        foreach (var role in roles)
        {
            if (!await roleManager.RoleExistsAsync(role.Name!))
                await roleManager.CreateAsync(role);
        }
    }

    private static async Task SeedAdminUserAsync(UserManager<ApplicationUser> userManager)
    {
        const string adminEmail = "admin@nickhr.local";
        if (await userManager.FindByEmailAsync(adminEmail) == null)
        {
            var admin = new ApplicationUser
            {
                UserName = adminEmail,
                Email = adminEmail,
                FirstName = "System",
                LastName = "Administrator",
                EmailConfirmed = true,
                IsActive = true
            };
            await userManager.CreateAsync(admin, "Admin@123456");
            await userManager.AddToRoleAsync(admin, "SuperAdmin");
        }
    }

    private static async Task SeedGradesAsync(NickHRDbContext context)
    {
        if (await context.Grades.AnyAsync()) return;

        var grades = new[]
        {
            new Grade { Name = "Grade 1 - Entry", Level = 1, MinSalary = 2000m, MidSalary = 3000m, MaxSalary = 4000m, Description = "Entry-level positions", IsActive = true },
            new Grade { Name = "Grade 2 - Junior", Level = 2, MinSalary = 3500m, MidSalary = 5000m, MaxSalary = 6500m, Description = "Junior positions", IsActive = true },
            new Grade { Name = "Grade 3 - Mid-Level", Level = 3, MinSalary = 5500m, MidSalary = 7500m, MaxSalary = 9500m, Description = "Mid-level positions", IsActive = true },
            new Grade { Name = "Grade 4 - Senior", Level = 4, MinSalary = 8000m, MidSalary = 11000m, MaxSalary = 14000m, Description = "Senior positions", IsActive = true },
            new Grade { Name = "Grade 5 - Lead", Level = 5, MinSalary = 12000m, MidSalary = 16000m, MaxSalary = 20000m, Description = "Lead/Supervisory positions", IsActive = true },
            new Grade { Name = "Grade 6 - Manager", Level = 6, MinSalary = 17000m, MidSalary = 22000m, MaxSalary = 28000m, Description = "Management positions", IsActive = true },
            new Grade { Name = "Grade 7 - Director", Level = 7, MinSalary = 25000m, MidSalary = 35000m, MaxSalary = 45000m, Description = "Director-level positions", IsActive = true },
            new Grade { Name = "Grade 8 - Executive", Level = 8, MinSalary = 40000m, MidSalary = 55000m, MaxSalary = 70000m, Description = "Executive positions", IsActive = true },
        };
        context.Grades.AddRange(grades);
        await context.SaveChangesAsync();
    }

    private static async Task SeedLocationsAsync(NickHRDbContext context)
    {
        if (await context.Locations.AnyAsync()) return;

        var locations = new[]
        {
            new Location { Name = "Head Office - Tema", Address = "Tema Port Area", City = "Tema", Region = "Greater Accra", Country = "Ghana", IsHeadOffice = true, IsActive = true },
            new Location { Name = "Accra Office", Address = "Airport City", City = "Accra", Region = "Greater Accra", Country = "Ghana", IsHeadOffice = false, IsActive = true },
            new Location { Name = "Takoradi Office", Address = "Takoradi Port Area", City = "Takoradi", Region = "Western", Country = "Ghana", IsHeadOffice = false, IsActive = true },
        };
        context.Locations.AddRange(locations);
        await context.SaveChangesAsync();
    }

    private static async Task SeedSalaryComponentsAsync(NickHRDbContext context)
    {
        if (await context.SalaryComponents.AnyAsync()) return;

        var components = new[]
        {
            // Earnings
            new SalaryComponent { Name = "Basic Salary", Code = "BASIC", ComponentType = SalaryComponentType.Earning, IsFixed = true, IsTaxable = true, IsStatutory = false, Description = "Basic monthly salary", IsActive = true },
            new SalaryComponent { Name = "Housing Allowance", Code = "HOUSING", ComponentType = SalaryComponentType.Earning, IsFixed = true, IsTaxable = true, IsStatutory = false, Description = "Monthly housing allowance", IsActive = true },
            new SalaryComponent { Name = "Transport Allowance", Code = "TRANSPORT", ComponentType = SalaryComponentType.Earning, IsFixed = true, IsTaxable = true, IsStatutory = false, Description = "Monthly transport allowance", IsActive = true },
            new SalaryComponent { Name = "Risk Allowance", Code = "RISK", ComponentType = SalaryComponentType.Earning, IsFixed = true, IsTaxable = true, IsStatutory = false, Description = "Risk/hazard allowance", IsActive = true },
            new SalaryComponent { Name = "Responsibility Allowance", Code = "RESP", ComponentType = SalaryComponentType.Earning, IsFixed = true, IsTaxable = true, IsStatutory = false, Description = "Responsibility allowance", IsActive = true },
            new SalaryComponent { Name = "Fuel Allowance", Code = "FUEL", ComponentType = SalaryComponentType.Earning, IsFixed = true, IsTaxable = true, IsStatutory = false, Description = "Monthly fuel allowance", IsActive = true },
            new SalaryComponent { Name = "Entertainment Allowance", Code = "ENTERTAIN", ComponentType = SalaryComponentType.Earning, IsFixed = true, IsTaxable = true, IsStatutory = false, Description = "Entertainment allowance", IsActive = true },
            new SalaryComponent { Name = "Overtime", Code = "OVERTIME", ComponentType = SalaryComponentType.Earning, IsFixed = false, IsTaxable = true, IsStatutory = false, Description = "Overtime pay", IsActive = true },

            // Deductions
            new SalaryComponent { Name = "SSNIT Employee (5.5%)", Code = "SSNIT_EE", ComponentType = SalaryComponentType.Deduction, IsFixed = false, IsTaxable = false, IsStatutory = true, DefaultPercentage = 5.5m, Description = "Employee SSNIT contribution (5.5% of basic)", IsActive = true },
            new SalaryComponent { Name = "SSNIT Employer (13%)", Code = "SSNIT_ER", ComponentType = SalaryComponentType.Deduction, IsFixed = false, IsTaxable = false, IsStatutory = true, DefaultPercentage = 13m, Description = "Employer SSNIT contribution (13% of basic)", IsActive = true },
            new SalaryComponent { Name = "Provident Fund", Code = "PROVIDENT", ComponentType = SalaryComponentType.Deduction, IsFixed = false, IsTaxable = false, IsStatutory = false, Description = "Voluntary provident fund", IsActive = true },
            new SalaryComponent { Name = "Loan Repayment", Code = "LOAN", ComponentType = SalaryComponentType.Deduction, IsFixed = false, IsTaxable = false, IsStatutory = false, Description = "Staff loan repayment", IsActive = true },
            new SalaryComponent { Name = "Union Dues", Code = "UNION", ComponentType = SalaryComponentType.Deduction, IsFixed = true, IsTaxable = false, IsStatutory = false, Description = "Trade union dues", IsActive = true },
            new SalaryComponent { Name = "Welfare Contribution", Code = "WELFARE", ComponentType = SalaryComponentType.Deduction, IsFixed = true, IsTaxable = false, IsStatutory = false, Description = "Staff welfare contribution", IsActive = true },
        };
        context.SalaryComponents.AddRange(components);
        await context.SaveChangesAsync();
    }

    private static async Task SeedLeavePoliciesAsync(NickHRDbContext context)
    {
        if (await context.LeavePolicies.AnyAsync()) return;

        var policies = new[]
        {
            new LeavePolicy { LeaveType = LeaveType.Annual, Name = "Annual Leave", DefaultDays = 15, MaxAccumulation = 30, CarryForwardMax = 5, IsCarryForwardAllowed = true, RequiresMedicalCertificate = false, MinServiceMonthsRequired = 12, Description = "Annual paid leave per Ghana Labour Act (min 15 days)", IsActive = true },
            new LeavePolicy { LeaveType = LeaveType.Sick, Name = "Sick Leave", DefaultDays = 30, MaxAccumulation = 30, CarryForwardMax = 0, IsCarryForwardAllowed = false, RequiresMedicalCertificate = true, MinServiceMonthsRequired = 0, Description = "Certified sick leave with full pay", IsActive = true },
            new LeavePolicy { LeaveType = LeaveType.Maternity, Name = "Maternity Leave", DefaultDays = 84, MaxAccumulation = 84, CarryForwardMax = 0, IsCarryForwardAllowed = false, RequiresMedicalCertificate = true, MinServiceMonthsRequired = 0, Description = "12 weeks maternity leave (14 for multiple births)", IsActive = true },
            new LeavePolicy { LeaveType = LeaveType.Paternity, Name = "Paternity Leave", DefaultDays = 5, MaxAccumulation = 5, CarryForwardMax = 0, IsCarryForwardAllowed = false, RequiresMedicalCertificate = false, MinServiceMonthsRequired = 0, Description = "5 days paternity leave", IsActive = true },
            new LeavePolicy { LeaveType = LeaveType.Casual, Name = "Casual Leave", DefaultDays = 5, MaxAccumulation = 5, CarryForwardMax = 0, IsCarryForwardAllowed = false, RequiresMedicalCertificate = false, MinServiceMonthsRequired = 0, Description = "Short-notice casual leave", IsActive = true },
            new LeavePolicy { LeaveType = LeaveType.Study, Name = "Study/Exam Leave", DefaultDays = 10, MaxAccumulation = 10, CarryForwardMax = 0, IsCarryForwardAllowed = false, RequiresMedicalCertificate = false, MinServiceMonthsRequired = 12, Description = "Leave for examinations and study", IsActive = true },
            new LeavePolicy { LeaveType = LeaveType.Compassionate, Name = "Compassionate Leave", DefaultDays = 5, MaxAccumulation = 5, CarryForwardMax = 0, IsCarryForwardAllowed = false, RequiresMedicalCertificate = false, MinServiceMonthsRequired = 0, Description = "Bereavement/family emergency leave", IsActive = true },
            new LeavePolicy { LeaveType = LeaveType.LeaveWithoutPay, Name = "Leave Without Pay", DefaultDays = 30, MaxAccumulation = 30, CarryForwardMax = 0, IsCarryForwardAllowed = false, RequiresMedicalCertificate = false, MinServiceMonthsRequired = 12, Description = "Unpaid leave", IsActive = true },
        };
        context.LeavePolicies.AddRange(policies);
        await context.SaveChangesAsync();
    }

    private static async Task SeedHolidaysAsync(NickHRDbContext context)
    {
        if (await context.Holidays.AnyAsync()) return;

        var year = DateTime.UtcNow.Year;
        var holidays = new[]
        {
            new Holiday { Name = "New Year's Day", Date = new DateTime(year, 1, 1), Year = year, IsRecurring = true },
            new Holiday { Name = "Independence Day", Date = new DateTime(year, 3, 6), Year = year, IsRecurring = true },
            new Holiday { Name = "Constitution Day", Date = new DateTime(year, 1, 7), Year = year, IsRecurring = true },
            new Holiday { Name = "May Day", Date = new DateTime(year, 5, 1), Year = year, IsRecurring = true },
            new Holiday { Name = "African Union Day", Date = new DateTime(year, 5, 25), Year = year, IsRecurring = true },
            new Holiday { Name = "Founders' Day", Date = new DateTime(year, 8, 4), Year = year, IsRecurring = true },
            new Holiday { Name = "Kwame Nkrumah Memorial Day", Date = new DateTime(year, 9, 21), Year = year, IsRecurring = true },
            new Holiday { Name = "Farmers' Day", Date = new DateTime(year, 12, 5), Year = year, IsRecurring = true, Description = "First Friday of December" },
            new Holiday { Name = "Christmas Day", Date = new DateTime(year, 12, 25), Year = year, IsRecurring = true },
            new Holiday { Name = "Boxing Day", Date = new DateTime(year, 12, 26), Year = year, IsRecurring = true },
        };
        context.Holidays.AddRange(holidays);
        await context.SaveChangesAsync();
    }

    private static async Task SeedCompanySettingsAsync(NickHRDbContext context)
    {
        if (await context.CompanySettings.AnyAsync()) return;

        var settings = new[]
        {
            new CompanySetting { Key = "Company.Name", Value = "NickHR Company Ltd", Description = "Company name", Category = "Company" },
            new CompanySetting { Key = "Company.Address", Value = "Tema, Greater Accra, Ghana", Description = "Company address", Category = "Company" },
            new CompanySetting { Key = "Company.Phone", Value = "+233 XX XXX XXXX", Description = "Company phone", Category = "Company" },
            new CompanySetting { Key = "Company.Email", Value = "hr@company.com", Description = "HR email", Category = "Company" },
            new CompanySetting { Key = "Company.FiscalYearStart", Value = "1", Description = "Fiscal year start month (1=January)", Category = "Company" },
            new CompanySetting { Key = "Company.Currency", Value = "GHS", Description = "Default currency", Category = "Company" },
            new CompanySetting { Key = "Company.CurrencySymbol", Value = "GH₵", Description = "Currency symbol", Category = "Company" },
            new CompanySetting { Key = "Payroll.SSNITEmployeeRate", Value = "5.5", Description = "Employee SSNIT contribution rate (%)", Category = "Payroll" },
            new CompanySetting { Key = "Payroll.SSNITEmployerRate", Value = "13", Description = "Employer SSNIT contribution rate (%)", Category = "Payroll" },
            new CompanySetting { Key = "Payroll.PayDay", Value = "25", Description = "Monthly payday", Category = "Payroll" },
            new CompanySetting { Key = "Leave.DefaultAnnualDays", Value = "15", Description = "Default annual leave days", Category = "Leave" },
            new CompanySetting { Key = "Leave.MaxCarryForward", Value = "5", Description = "Max leave carry-forward days", Category = "Leave" },
            new CompanySetting { Key = "Attendance.WorkStartTime", Value = "08:00", Description = "Standard work start time", Category = "Attendance" },
            new CompanySetting { Key = "Attendance.WorkEndTime", Value = "17:00", Description = "Standard work end time", Category = "Attendance" },
            new CompanySetting { Key = "Attendance.GracePeriodMinutes", Value = "15", Description = "Late arrival grace period (minutes)", Category = "Attendance" },
        };
        context.CompanySettings.AddRange(settings);
        await context.SaveChangesAsync();
    }

    private static async Task SeedEmailTemplatesAsync(NickHRDbContext context)
    {
        if (await context.EmailTemplates.AnyAsync()) return;

        var templates = new[]
        {
            new EmailTemplate { Name = "Welcome Email", Code = "WELCOME", Subject = "Welcome to {{CompanyName}}", Body = "<h2>Welcome, {{EmployeeName}}!</h2><p>We are excited to have you join our team at {{CompanyName}}. Your employee ID is {{EmployeeCode}}.</p><p>Your start date is {{StartDate}}.</p>", Description = "Sent to new employees on creation", IsActive = true },
            new EmailTemplate { Name = "Leave Approved", Code = "LEAVE_APPROVED", Subject = "Leave Request Approved", Body = "<p>Dear {{EmployeeName}},</p><p>Your {{LeaveType}} leave request from {{StartDate}} to {{EndDate}} ({{Days}} days) has been approved by {{ApproverName}}.</p>", Description = "Sent when leave is approved", IsActive = true },
            new EmailTemplate { Name = "Leave Rejected", Code = "LEAVE_REJECTED", Subject = "Leave Request Rejected", Body = "<p>Dear {{EmployeeName}},</p><p>Your {{LeaveType}} leave request from {{StartDate}} to {{EndDate}} has been rejected.</p><p>Reason: {{Reason}}</p>", Description = "Sent when leave is rejected", IsActive = true },
            new EmailTemplate { Name = "Payslip Notification", Code = "PAYSLIP", Subject = "Your Payslip for {{Month}} {{Year}}", Body = "<p>Dear {{EmployeeName}},</p><p>Your payslip for {{Month}} {{Year}} is now available. Net pay: {{CurrencySymbol}}{{NetPay}}.</p><p>Please log in to the portal to view your detailed payslip.</p>", Description = "Monthly payslip notification", IsActive = true },
            new EmailTemplate { Name = "Password Reset", Code = "PASSWORD_RESET", Subject = "Password Reset Request", Body = "<p>Dear {{EmployeeName}},</p><p>A password reset has been requested for your account. Click the link below to reset your password:</p><p><a href='{{ResetLink}}'>Reset Password</a></p>", Description = "Password reset email", IsActive = true },
            new EmailTemplate { Name = "User Invitation", Code = "USER_INVITATION", Subject = "Welcome to NickHR — Your Account Details", Body = "<div style='font-family:Arial,sans-serif;max-width:600px;margin:0 auto'><h2 style='color:#4F46E5'>Welcome to NickHR!</h2><p>Dear {{EmployeeName}},</p><p>An account has been created for you on the NickHR Human Resource Management System.</p><table style='border-collapse:collapse;width:100%;margin:16px 0'><tr><td style='padding:8px;border:1px solid #ddd;background:#f8f9fa;font-weight:bold'>Login URL</td><td style='padding:8px;border:1px solid #ddd'><a href='{{LoginUrl}}'>{{LoginUrl}}</a></td></tr><tr><td style='padding:8px;border:1px solid #ddd;background:#f8f9fa;font-weight:bold'>Email</td><td style='padding:8px;border:1px solid #ddd'>{{Email}}</td></tr><tr><td style='padding:8px;border:1px solid #ddd;background:#f8f9fa;font-weight:bold'>Temporary Password</td><td style='padding:8px;border:1px solid #ddd;font-family:monospace;font-size:16px;color:#c62828'>{{TempPassword}}</td></tr></table><p style='color:#c62828;font-weight:bold'>You will be required to change your password on first login.</p><p>If you have any questions, please contact your HR administrator.</p><p>Best regards,<br/>NickHR System</p></div>", Description = "Sent when a new user account is created", IsActive = true },
        };
        context.EmailTemplates.AddRange(templates);
        await context.SaveChangesAsync();
    }

    // Ensure new templates exist for existing deployments (runs every startup)
    public static async Task EnsureNewTemplatesAsync(NickHRDbContext context)
    {
        if (!await context.EmailTemplates.AnyAsync(t => t.Code == "USER_INVITATION"))
        {
            context.EmailTemplates.Add(new EmailTemplate { Name = "User Invitation", Code = "USER_INVITATION", Subject = "Welcome to NickHR — Your Account Details", Body = "<div style='font-family:Arial,sans-serif;max-width:600px;margin:0 auto'><h2 style='color:#4F46E5'>Welcome to NickHR!</h2><p>Dear {{EmployeeName}},</p><p>An account has been created for you on the NickHR Human Resource Management System.</p><table style='border-collapse:collapse;width:100%;margin:16px 0'><tr><td style='padding:8px;border:1px solid #ddd;background:#f8f9fa;font-weight:bold'>Login URL</td><td style='padding:8px;border:1px solid #ddd'><a href='{{LoginUrl}}'>{{LoginUrl}}</a></td></tr><tr><td style='padding:8px;border:1px solid #ddd;background:#f8f9fa;font-weight:bold'>Email</td><td style='padding:8px;border:1px solid #ddd'>{{Email}}</td></tr><tr><td style='padding:8px;border:1px solid #ddd;background:#f8f9fa;font-weight:bold'>Temporary Password</td><td style='padding:8px;border:1px solid #ddd;font-family:monospace;font-size:16px;color:#c62828'>{{TempPassword}}</td></tr></table><p style='color:#c62828;font-weight:bold'>You will be required to change your password on first login.</p><p>If you have any questions, please contact your HR administrator.</p><p>Best regards,<br/>NickHR System</p></div>", Description = "Sent when a new user account is created", IsActive = true });
            await context.SaveChangesAsync();
        }
    }
}
