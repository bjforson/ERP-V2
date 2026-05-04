using Microsoft.EntityFrameworkCore;
using NickHR.Core.Entities.System;
using NickHR.Core.Enums;
using NickHR.Core.Interfaces;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Common;

public class NotificationService : INotificationService
{
    private readonly NickHRDbContext _db;

    public NotificationService(NickHRDbContext db)
    {
        _db = db;
    }

    public async Task SendAsync(int employeeId, string title, string message, NotificationType type)
    {
        var notification = new Notification
        {
            RecipientEmployeeId = employeeId,
            Title = title,
            Message = message,
            NotificationType = type,
            IsRead = false
        };

        _db.Notifications.Add(notification);
        await _db.SaveChangesAsync();
    }

    public async Task<int> GetUnreadCountAsync(int employeeId)
    {
        return await _db.Notifications
            .CountAsync(n => n.RecipientEmployeeId == employeeId &&
                             !n.IsRead &&
                             !n.IsDeleted);
    }

    public async Task MarkAsReadAsync(int notificationId)
    {
        var notification = await _db.Notifications
            .FirstOrDefaultAsync(n => n.Id == notificationId && !n.IsDeleted)
            ?? throw new KeyNotFoundException($"Notification with ID {notificationId} not found.");

        notification.IsRead = true;
        notification.ReadAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }
}
