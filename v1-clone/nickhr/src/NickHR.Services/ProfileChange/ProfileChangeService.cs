using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NickHR.Core.Entities.Core;
using NickHR.Core.Enums;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.ProfileChange;

/// <summary>
/// Manages employee profile change requests with tier-based approval routing.
/// Tier 1 fields are auto-approved; Tier 2 fields require HR review.
/// SuperAdmin role bypasses Tier 2 approval.
/// </summary>
public class ProfileChangeService
{
    private readonly IDbContextFactory<NickHRDbContext> _dbFactory;
    private readonly UserManager<ApplicationUser> _userManager;

    public ProfileChangeService(
        IDbContextFactory<NickHRDbContext> dbFactory,
        UserManager<ApplicationUser> userManager)
    {
        _dbFactory = dbFactory;
        _userManager = userManager;
    }

    // ── Field Configuration ───────────────────────────────────────────────────
    private static readonly Dictionary<string, (int Tier, string Label)> FieldConfig = new()
    {
        // Tier 1 – Auto-approve (low-sensitivity personal details)
        { "PersonalEmail",       (1, "Personal Email") },
        { "SecondaryPhone",      (1, "Secondary Phone") },
        { "ResidentialAddress",  (1, "Residential Address") },
        { "PostalAddress",       (1, "Postal Address") },
        { "Hometown",            (1, "Hometown") },
        { "Region",              (1, "Region") },
        { "Religion",            (1, "Religion") },
        { "GhanaPostGPS",        (1, "GhanaPost GPS Address") },
        { "MedicalConditions",   (1, "Medical Conditions") },
        { "PlaceOfBirth",        (1, "Place of Birth") },
        { "EthnicGroup",         (1, "Ethnic Group") },

        // Tier 2 – Requires HR approval (identity / financial / sensitive fields)
        { "FirstName",               (2, "First Name") },
        { "MiddleName",              (2, "Middle Name") },
        { "LastName",                (2, "Last Name") },
        { "PrimaryPhone",            (2, "Primary Phone") },
        { "WorkEmail",               (2, "Work Email") },
        { "DateOfBirth",             (2, "Date of Birth") },
        { "Gender",                  (2, "Gender") },
        { "MaritalStatus",           (2, "Marital Status") },
        { "Nationality",             (2, "Nationality") },
        { "GhanaCardNumber",         (2, "Ghana Card Number") },
        { "TIN",                     (2, "Tax Identification Number") },
        { "SSNITNumber",             (2, "SSNIT Number") },
        { "BankName",                (2, "Bank Name") },
        { "BankBranch",              (2, "Bank Branch") },
        { "BankAccountNumber",       (2, "Bank Account Number") },
        { "MobileMoneyNumber",       (2, "Mobile Money Number") },
        { "PassportNumber",          (2, "Passport Number") },
        { "PassportExpiry",          (2, "Passport Expiry") },
        { "DriversLicenseNumber",    (2, "Driver's License Number") },
        { "DriversLicenseExpiry",    (2, "Driver's License Expiry") },
        { "SpouseName",              (2, "Spouse Name") },
        { "SpousePhone",             (2, "Spouse Phone") },
        { "SpouseEmployer",          (2, "Spouse Employer") },
        { "NumberOfChildren",        (2, "Number of Children") },
        { "MothersMaidenName",       (2, "Mother's Maiden Name") },
        { "Tier2PensionNumber",      (2, "Tier 2 Pension Number") },
        { "Tier2Provider",           (2, "Tier 2 Provider") },
        { "Tier3PensionNumber",      (2, "Tier 3 Pension Number") },
        { "Tier3Provider",           (2, "Tier 3 Provider") },
        { "BloodGroup",              (2, "Blood Group") },
    };

    // ── Public Query Helpers ──────────────────────────────────────────────────

    public int GetFieldTier(string fieldName)
    {
        if (FieldConfig.TryGetValue(fieldName, out var cfg)) return cfg.Tier;
        return 3; // Not editable via self-service
    }

    public IReadOnlyList<(string FieldName, string Label, int Tier)> GetEditableFields()
        => FieldConfig
            .Select(kv => (kv.Key, kv.Value.Label, kv.Value.Tier))
            .OrderBy(f => f.Tier)
            .ThenBy(f => f.Label)
            .ToList();

    // ── Core Operations ───────────────────────────────────────────────────────

    /// <summary>
    /// Submit a change request for a single field.
    /// Tier 1: applied immediately and recorded as Approved.
    /// Tier 2: recorded as Pending; HR must approve before the field is written.
    /// SuperAdmin: Tier 2 is treated as Tier 1 (auto-approved).
    /// </summary>
    public async Task<ProfileChangeRequest> SubmitChangeAsync(
        int employeeId,
        string fieldName,
        string? newValue,
        string? reason,
        bool isSuperAdmin = false)
    {
        if (!FieldConfig.TryGetValue(fieldName, out var cfg))
            throw new InvalidOperationException($"Field '{fieldName}' is not editable via self-service.");

        await using var ctx = await _dbFactory.CreateDbContextAsync();

        var employee = await ctx.Employees.FindAsync(employeeId)
                       ?? throw new InvalidOperationException($"Employee {employeeId} not found.");

        // Snapshot current value
        var oldValue = GetCurrentValue(employee, fieldName);

        // Determine effective tier (SuperAdmin bypasses Tier 2)
        var effectiveTier = (isSuperAdmin && cfg.Tier == 2) ? 1 : cfg.Tier;

        var request = new ProfileChangeRequest
        {
            EmployeeId  = employeeId,
            FieldName   = fieldName,
            FieldLabel  = cfg.Label,
            OldValue    = oldValue,
            NewValue    = newValue,
            Reason      = reason,
            Tier        = cfg.Tier,
            Status      = ChangeRequestStatus.Pending,
            CreatedAt   = DateTime.UtcNow,
        };

        if (effectiveTier == 1)
        {
            // Auto-approve: apply immediately
            ApplyChange(employee, fieldName, newValue);
            employee.UpdatedAt = DateTime.UtcNow;

            request.Status      = ChangeRequestStatus.Approved;
            request.ReviewedAt  = DateTime.UtcNow;
            request.AppliedAt   = DateTime.UtcNow;
        }

        ctx.ProfileChangeRequests.Add(request);
        await ctx.SaveChangesAsync();

        return request;
    }

    /// <summary>Returns all Pending change requests (HR queue).</summary>
    public async Task<List<ProfileChangeRequest>> GetPendingRequestsAsync()
    {
        await using var ctx = await _dbFactory.CreateDbContextAsync();
        return await ctx.ProfileChangeRequests
            .Include(r => r.Employee)
            .Where(r => r.Status == ChangeRequestStatus.Pending && !r.IsDeleted)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync();
    }

    /// <summary>Returns all change requests for a specific employee.</summary>
    public async Task<List<ProfileChangeRequest>> GetMyRequestsAsync(int employeeId)
    {
        await using var ctx = await _dbFactory.CreateDbContextAsync();
        return await ctx.ProfileChangeRequests
            .Include(r => r.ReviewedBy)
            .Where(r => r.EmployeeId == employeeId && !r.IsDeleted)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();
    }

    /// <summary>Returns all requests (for HR history view), optionally filtered by status.</summary>
    public async Task<List<ProfileChangeRequest>> GetAllRequestsAsync(ChangeRequestStatus? status = null)
    {
        await using var ctx = await _dbFactory.CreateDbContextAsync();
        var query = ctx.ProfileChangeRequests
            .Include(r => r.Employee)
            .Include(r => r.ReviewedBy)
            .Where(r => !r.IsDeleted);

        if (status.HasValue)
            query = query.Where(r => r.Status == status.Value);

        return await query.OrderByDescending(r => r.CreatedAt).ToListAsync();
    }

    /// <summary>HR approves a pending change request; the field is then written to the Employee record.</summary>
    public async Task<ProfileChangeRequest> ApproveRequestAsync(int requestId, int reviewerId)
    {
        await using var ctx = await _dbFactory.CreateDbContextAsync();

        var request = await ctx.ProfileChangeRequests
            .Include(r => r.Employee)
            .FirstOrDefaultAsync(r => r.Id == requestId && !r.IsDeleted)
            ?? throw new InvalidOperationException($"Request {requestId} not found.");

        if (request.Status != ChangeRequestStatus.Pending)
            throw new InvalidOperationException("Only Pending requests can be approved.");

        var employee = await ctx.Employees.FindAsync(request.EmployeeId)
                       ?? throw new InvalidOperationException("Employee not found.");

        ApplyChange(employee, request.FieldName, request.NewValue);
        employee.UpdatedAt = DateTime.UtcNow;

        request.Status      = ChangeRequestStatus.Approved;
        request.ReviewedById = reviewerId;
        request.ReviewedAt  = DateTime.UtcNow;
        request.AppliedAt   = DateTime.UtcNow;
        request.UpdatedAt   = DateTime.UtcNow;

        await ctx.SaveChangesAsync();
        return request;
    }

    /// <summary>HR rejects a pending change request with an optional reason.</summary>
    public async Task<ProfileChangeRequest> RejectRequestAsync(int requestId, int reviewerId, string? reason)
    {
        await using var ctx = await _dbFactory.CreateDbContextAsync();

        var request = await ctx.ProfileChangeRequests
            .FirstOrDefaultAsync(r => r.Id == requestId && !r.IsDeleted)
            ?? throw new InvalidOperationException($"Request {requestId} not found.");

        if (request.Status != ChangeRequestStatus.Pending)
            throw new InvalidOperationException("Only Pending requests can be rejected.");

        request.Status          = ChangeRequestStatus.Rejected;
        request.ReviewedById    = reviewerId;
        request.ReviewedAt      = DateTime.UtcNow;
        request.RejectionReason = reason;
        request.UpdatedAt       = DateTime.UtcNow;

        await ctx.SaveChangesAsync();
        return request;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? GetCurrentValue(NickHR.Core.Entities.Core.Employee employee, string fieldName)
    {
        var prop = typeof(NickHR.Core.Entities.Core.Employee).GetProperty(fieldName);
        if (prop == null) return null;
        var value = prop.GetValue(employee);
        return value?.ToString();
    }

    private static void ApplyChange(NickHR.Core.Entities.Core.Employee employee, string fieldName, string? newValue)
    {
        var prop = typeof(NickHR.Core.Entities.Core.Employee).GetProperty(fieldName);
        if (prop == null) return;

        var type = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
        object? convertedValue = null;

        if (!string.IsNullOrEmpty(newValue))
        {
            if (type == typeof(string))
                convertedValue = newValue;
            else if (type == typeof(int))
                convertedValue = int.Parse(newValue);
            else if (type == typeof(decimal))
                convertedValue = decimal.Parse(newValue);
            else if (type == typeof(DateTime))
                convertedValue = DateTime.Parse(newValue);
            else if (type == typeof(DateOnly))
                convertedValue = DateOnly.Parse(newValue);
            else if (type.IsEnum)
                convertedValue = Enum.Parse(type, newValue);
            else
                convertedValue = Convert.ChangeType(newValue, type);
        }

        prop.SetValue(employee, convertedValue);
    }
}
