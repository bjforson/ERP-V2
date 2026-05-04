using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NickHR.Core.DTOs;
using NickHR.Core.Entities.System;
using NickHR.Core.Interfaces;
using NickHR.Infrastructure.Data;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly INotificationService _notificationService;
    private readonly ICurrentUserService _currentUser;
    private readonly NickHRDbContext _db;

    public NotificationsController(
        INotificationService notificationService,
        ICurrentUserService currentUser,
        NickHRDbContext db)
    {
        _notificationService = notificationService;
        _currentUser = currentUser;
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<Notification>>>> GetMyNotifications()
    {
        var employeeId = _currentUser.EmployeeId;
        if (employeeId is null)
            return BadRequest(ApiResponse<List<Notification>>.Fail("No employee profile linked to current user."));

        var notifications = await _db.Notifications
            .Where(n => n.RecipientEmployeeId == employeeId.Value)
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync();

        return Ok(ApiResponse<List<Notification>>.Ok(notifications));
    }

    [HttpGet("unread-count")]
    public async Task<ActionResult<ApiResponse<int>>> GetUnreadCount()
    {
        var employeeId = _currentUser.EmployeeId;
        if (employeeId is null)
            return BadRequest(ApiResponse<int>.Fail("No employee profile linked to current user."));

        var count = await _notificationService.GetUnreadCountAsync(employeeId.Value);
        return Ok(ApiResponse<int>.Ok(count));
    }

    [HttpPost("{id:int}/read")]
    public async Task<ActionResult<ApiResponse<object>>> MarkAsRead(int id)
    {
        try
        {
            await _notificationService.MarkAsReadAsync(id);
            return Ok(ApiResponse<object>.Ok(null, "Notification marked as read."));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }
}
