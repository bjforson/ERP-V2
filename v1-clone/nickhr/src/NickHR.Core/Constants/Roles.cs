namespace NickHR.Core.Constants;

/// <summary>
/// String constants for role names used in <c>[Authorize(Roles = ...)]</c> attributes.
/// Avoids magic-string drift (e.g. "HrManager" vs "HRManager") and gives one place to
/// rename if the seed data changes.
/// </summary>
public static class Roles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string HRManager = "HRManager";
    public const string HROfficer = "HROfficer";
    public const string DepartmentManager = "DepartmentManager";
    public const string PayrollAdmin = "PayrollAdmin";
    public const string FinanceOfficer = "FinanceOfficer";
}

/// <summary>
/// Pre-composed comma-joined role lists for the most common authorization combinations.
/// <c>[Authorize(Roles = RoleSets.HRStaff)]</c> reads cleaner than typing out the
/// "SuperAdmin,HRManager,HROfficer" literal everywhere, and a misspelling here is
/// caught at compile time instead of silently locking everyone out.
/// </summary>
public static class RoleSets
{
    /// <summary>SuperAdmin only.</summary>
    public const string SuperAdminOnly = Roles.SuperAdmin;

    /// <summary>SuperAdmin + HRManager.</summary>
    public const string SeniorHR = Roles.SuperAdmin + "," + Roles.HRManager;

    /// <summary>SuperAdmin + DepartmentManager.</summary>
    public const string AdminOrDeptManager = Roles.SuperAdmin + "," + Roles.DepartmentManager;

    /// <summary>SuperAdmin + FinanceOfficer.</summary>
    public const string AdminOrFinance = Roles.SuperAdmin + "," + Roles.FinanceOfficer;

    /// <summary>SuperAdmin + HRManager + HROfficer — the canonical "HR staff" set.</summary>
    public const string HRStaff = Roles.SuperAdmin + "," + Roles.HRManager + "," + Roles.HROfficer;

    /// <summary>SuperAdmin + HRManager + DepartmentManager.</summary>
    public const string SeniorHROrDeptManager = Roles.SuperAdmin + "," + Roles.HRManager + "," + Roles.DepartmentManager;

    /// <summary>SuperAdmin + HRManager + FinanceOfficer.</summary>
    public const string SeniorHROrFinance = Roles.SuperAdmin + "," + Roles.HRManager + "," + Roles.FinanceOfficer;

    /// <summary>SuperAdmin + HRManager + PayrollAdmin.</summary>
    public const string SeniorHROrPayroll = Roles.SuperAdmin + "," + Roles.HRManager + "," + Roles.PayrollAdmin;

    /// <summary>HR staff + DepartmentManager (managers can act on their team).</summary>
    public const string HRStaffOrDeptManager = HRStaff + "," + Roles.DepartmentManager;

    /// <summary>HR staff + FinanceOfficer.</summary>
    public const string HRStaffOrFinance = HRStaff + "," + Roles.FinanceOfficer;

    /// <summary>HR staff + PayrollAdmin.</summary>
    public const string HRStaffOrPayroll = HRStaff + "," + Roles.PayrollAdmin;

    /// <summary>HR staff + DepartmentManager + PayrollAdmin.</summary>
    public const string HRStaffPlusDeptAndPayroll = HRStaff + "," + Roles.DepartmentManager + "," + Roles.PayrollAdmin;

    /// <summary>HR staff + FinanceOfficer + DepartmentManager.</summary>
    public const string HRStaffPlusFinanceAndDept = HRStaff + "," + Roles.FinanceOfficer + "," + Roles.DepartmentManager;
}
