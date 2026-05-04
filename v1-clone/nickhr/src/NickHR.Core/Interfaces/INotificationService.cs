using NickHR.Core.Enums;

namespace NickHR.Core.Interfaces;

public interface INotificationService
{
    Task SendAsync(int employeeId, string title, string message, NotificationType type);
    Task<int> GetUnreadCountAsync(int employeeId);
    Task MarkAsReadAsync(int notificationId);
}
