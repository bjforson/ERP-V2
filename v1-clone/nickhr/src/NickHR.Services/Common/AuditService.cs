using NickHR.Core.Entities.System;
using NickHR.Core.Interfaces;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Common;

public class AuditService : IAuditService
{
    private readonly NickHRDbContext _db;

    public AuditService(NickHRDbContext db)
    {
        _db = db;
    }

    public async Task LogAsync(
        string userId,
        string action,
        string entityType,
        string entityId,
        string? oldValues,
        string? newValues,
        string? ipAddress)
    {
        var entry = new AuditLog
        {
            UserId = userId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            OldValues = oldValues,
            NewValues = newValues,
            IPAddress = ipAddress,
            Timestamp = DateTime.UtcNow
        };

        _db.AuditLogs.Add(entry);
        await _db.SaveChangesAsync();
    }
}
