namespace NickHR.Core.Interfaces;

public interface ICurrentUserService
{
    string UserId { get; }
    string UserName { get; }
    int? EmployeeId { get; }
    string Role { get; }
    bool IsAuthenticated { get; }

    /// <summary>
    /// Returns true if the current user is allowed to view/operate on data
    /// belonging to <paramref name="employeeId"/>.
    /// Allow conditions:
    ///   - The current user IS that employee (EmployeeId claim matches), OR
    ///   - The current user is in any of the supplied <paramref name="privilegedRoles"/>
    ///     (e.g. SuperAdmin / HRManager / HROfficer / PayrollAdmin).
    /// Used for IDOR-prevention checks on per-employee endpoints
    /// (payslip, photo, document, letter, profile).
    /// </summary>
    Task<bool> CanAccessEmployeeAsync(int employeeId, params string[] privilegedRoles);
}
