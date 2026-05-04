using Microsoft.EntityFrameworkCore;
using NickHR.Core.Entities.Core;
using NickHR.Core.Enums;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Meeting;

public class MeetingService
{
    private readonly NickHRDbContext _db;

    public MeetingService(NickHRDbContext db)
    {
        _db = db;
    }

    public async Task<OneOnOneMeeting> ScheduleAsync(int managerId, int employeeId, DateTime scheduledDate)
    {
        var meeting = new OneOnOneMeeting
        {
            ManagerId = managerId,
            EmployeeId = employeeId,
            ScheduledDate = scheduledDate,
            Status = MeetingStatus.Scheduled
        };

        _db.OneOnOneMeetings.Add(meeting);
        await _db.SaveChangesAsync();
        return meeting;
    }

    public async Task<OneOnOneMeeting> CompleteAsync(
        int meetingId, string? notes, string? actionItems, DateTime? nextMeetingDate)
    {
        var meeting = await _db.OneOnOneMeetings.FindAsync(meetingId)
            ?? throw new KeyNotFoundException("Meeting not found.");

        meeting.Status = MeetingStatus.Completed;
        meeting.CompletedDate = DateTime.UtcNow;
        meeting.Notes = notes;
        meeting.ActionItems = actionItems;
        meeting.NextMeetingDate = nextMeetingDate;

        await _db.SaveChangesAsync();
        return meeting;
    }

    public async Task<OneOnOneMeeting> CancelAsync(int meetingId)
    {
        var meeting = await _db.OneOnOneMeetings.FindAsync(meetingId)
            ?? throw new KeyNotFoundException("Meeting not found.");

        if (meeting.Status == MeetingStatus.Completed)
            throw new InvalidOperationException("Cannot cancel a completed meeting.");

        meeting.Status = MeetingStatus.Cancelled;
        await _db.SaveChangesAsync();
        return meeting;
    }

    public async Task<List<object>> GetUpcomingAsync(int? managerId = null)
    {
        var now = DateTime.UtcNow;
        var query = _db.OneOnOneMeetings
            .Where(m => m.Status == MeetingStatus.Scheduled && m.ScheduledDate >= now);

        if (managerId.HasValue)
            query = query.Where(m => m.ManagerId == managerId.Value);

        return await query
            .OrderBy(m => m.ScheduledDate)
            .Select(m => (object)new
            {
                m.Id,
                m.ScheduledDate,
                m.Status,
                m.NextMeetingDate,
                Manager = m.Manager.FirstName + " " + m.Manager.LastName,
                Employee = m.Employee.FirstName + " " + m.Employee.LastName,
                m.ManagerId,
                m.EmployeeId
            })
            .ToListAsync();
    }

    public async Task<List<object>> GetHistoryAsync(int employeeId)
    {
        return await _db.OneOnOneMeetings
            .Where(m => m.EmployeeId == employeeId && m.Status == MeetingStatus.Completed)
            .OrderByDescending(m => m.CompletedDate)
            .Select(m => (object)new
            {
                m.Id,
                m.ScheduledDate,
                m.CompletedDate,
                m.Notes,
                m.ActionItems,
                m.NextMeetingDate,
                Manager = m.Manager.FirstName + " " + m.Manager.LastName
            })
            .ToListAsync();
    }

    public async Task<List<object>> GetMyMeetingsAsync(int employeeId)
    {
        return await _db.OneOnOneMeetings
            .Where(m => m.EmployeeId == employeeId || m.ManagerId == employeeId)
            .OrderByDescending(m => m.ScheduledDate)
            .Select(m => (object)new
            {
                m.Id,
                m.ScheduledDate,
                m.CompletedDate,
                m.Status,
                m.Notes,
                m.ActionItems,
                m.NextMeetingDate,
                Manager = m.Manager.FirstName + " " + m.Manager.LastName,
                Employee = m.Employee.FirstName + " " + m.Employee.LastName,
                m.ManagerId,
                m.EmployeeId,
                Role = m.ManagerId == employeeId ? "Manager" : "Employee"
            })
            .ToListAsync();
    }
}
