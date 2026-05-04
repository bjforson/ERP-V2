using Microsoft.EntityFrameworkCore;
using NickHR.Core.Entities.Discipline;
using NickHR.Core.Enums;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Discipline;

public interface IDisciplineService
{
    // Disciplinary Cases
    Task<DisciplinaryCase> CreateCaseAsync(int employeeId, DateTime incidentDate, string description,
        string? witnesses, string? evidence);
    Task<List<DisciplinaryCase>> GetCasesAsync(string? status = null);
    Task<DisciplinaryCase?> GetCaseByIdAsync(int id);
    Task<DisciplinaryCase> UpdateCaseStatusAsync(int id, string status, string? hearingNotes, string? panelMembers);
    Task<Warning> RecordActionAsync(int caseId, DisciplinaryAction action, int issuedById, string description);

    // Grievances
    Task<Grievance> CreateGrievanceAsync(int? employeeId, string subject, string description, bool isAnonymous);
    Task<List<Grievance>> GetGrievancesAsync(string? status = null);
    Task<Grievance> AssignGrievanceAsync(int id, int assignedToId);
    Task<Grievance> ResolveGrievanceAsync(int id, string resolution);
}

public class DisciplineService : IDisciplineService
{
    private readonly NickHRDbContext _db;

    public DisciplineService(NickHRDbContext db)
    {
        _db = db;
    }

    // ─── Disciplinary Cases ──────────────────────────────────────────────────

    public async Task<DisciplinaryCase> CreateCaseAsync(int employeeId, DateTime incidentDate,
        string description, string? witnesses, string? evidence)
    {
        var disciplinaryCase = new DisciplinaryCase
        {
            EmployeeId = employeeId,
            IncidentDate = incidentDate,
            Description = description,
            Witnesses = witnesses,
            Evidence = evidence,
            Status = "Open"
        };

        _db.DisciplinaryCases.Add(disciplinaryCase);
        await _db.SaveChangesAsync();
        return disciplinaryCase;
    }

    public async Task<List<DisciplinaryCase>> GetCasesAsync(string? status = null)
    {
        var query = _db.DisciplinaryCases
            .Where(c => !c.IsDeleted)
            .Include(c => c.Employee)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(c => c.Status == status);

        return await query.OrderByDescending(c => c.IncidentDate).ToListAsync();
    }

    public async Task<DisciplinaryCase?> GetCaseByIdAsync(int id)
    {
        return await _db.DisciplinaryCases
            .Where(c => c.Id == id && !c.IsDeleted)
            .Include(c => c.Employee)
            .Include(c => c.Warnings)
                .ThenInclude(w => w.IssuedBy)
            .FirstOrDefaultAsync();
    }

    public async Task<DisciplinaryCase> UpdateCaseStatusAsync(int id, string status,
        string? hearingNotes, string? panelMembers)
    {
        var disciplinaryCase = await _db.DisciplinaryCases.FindAsync(id)
            ?? throw new KeyNotFoundException($"Disciplinary case {id} not found.");

        disciplinaryCase.Status = status;

        if (hearingNotes is not null)
            disciplinaryCase.HearingNotes = hearingNotes;

        if (panelMembers is not null)
            disciplinaryCase.PanelMembers = panelMembers;

        disciplinaryCase.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return disciplinaryCase;
    }

    public async Task<Warning> RecordActionAsync(int caseId, DisciplinaryAction action,
        int issuedById, string description)
    {
        var disciplinaryCase = await _db.DisciplinaryCases.FindAsync(caseId)
            ?? throw new KeyNotFoundException($"Disciplinary case {caseId} not found.");

        var warning = new Warning
        {
            DisciplinaryCaseId = caseId,
            EmployeeId = disciplinaryCase.EmployeeId,
            WarningType = action,
            IssueDate = DateTime.UtcNow,
            Description = description,
            IssuedById = issuedById
        };

        _db.Warnings.Add(warning);

        disciplinaryCase.Action = action;
        disciplinaryCase.ActionDate = DateTime.UtcNow;
        disciplinaryCase.Status = "ActionTaken";
        disciplinaryCase.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return warning;
    }

    // ─── Grievances ──────────────────────────────────────────────────────────

    public async Task<Grievance> CreateGrievanceAsync(int? employeeId, string subject,
        string description, bool isAnonymous)
    {
        // PRIVACY: anonymous grievances must carry no linkable EmployeeId. Schema
        // is now `int?` so we store `null` rather than the previous `0` sentinel.
        var grievance = new Grievance
        {
            EmployeeId = isAnonymous ? null : employeeId,
            Subject = subject,
            Description = description,
            IsAnonymous = isAnonymous,
            FiledDate = DateTime.UtcNow,
            Status = "Filed"
        };

        _db.Grievances.Add(grievance);
        await _db.SaveChangesAsync();
        return grievance;
    }

    public async Task<List<Grievance>> GetGrievancesAsync(string? status = null)
    {
        var query = _db.Grievances
            .Where(g => !g.IsDeleted)
            .Include(g => g.Employee)
            .Include(g => g.AssignedTo)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(g => g.Status == status);

        return await query.OrderByDescending(g => g.FiledDate).ToListAsync();
    }

    public async Task<Grievance> AssignGrievanceAsync(int id, int assignedToId)
    {
        var grievance = await _db.Grievances.FindAsync(id)
            ?? throw new KeyNotFoundException($"Grievance {id} not found.");

        grievance.AssignedToId = assignedToId;
        grievance.Status = "UnderInvestigation";
        grievance.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return grievance;
    }

    public async Task<Grievance> ResolveGrievanceAsync(int id, string resolution)
    {
        var grievance = await _db.Grievances.FindAsync(id)
            ?? throw new KeyNotFoundException($"Grievance {id} not found.");

        grievance.Resolution = resolution;
        grievance.Status = "Resolved";
        grievance.ResolvedAt = DateTime.UtcNow;
        grievance.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return grievance;
    }
}
