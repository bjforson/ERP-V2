namespace NickHR.Core.Interfaces;

public interface IAuditService
{
    Task LogAsync(
        string userId,
        string action,
        string entityType,
        string entityId,
        string? oldValues,
        string? newValues,
        string? ipAddress);
}
