namespace NickFinance.WebApp.Services;

/// <summary>
/// In-process "who is this caller" service. v1 reads from a configured
/// dev user; production wires this to the real Identity layer
/// (NickERP.Platform.Identity) once it's adopted by the host.
/// </summary>
public sealed class CurrentUser
{
    public Guid UserId { get; }
    public string DisplayName { get; }
    public string Email { get; }
    public long TenantId { get; }

    public CurrentUser(Guid userId, string displayName, string email, long tenantId)
    {
        UserId = userId;
        DisplayName = displayName;
        Email = email;
        TenantId = tenantId;
    }
}
