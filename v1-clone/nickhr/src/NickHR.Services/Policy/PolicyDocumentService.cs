using Microsoft.EntityFrameworkCore;
using NickHR.Core.Entities.System;
using NickHR.Core.Interfaces;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Policy;

public class PolicyDocumentService : IPolicyDocumentService
{
    private readonly NickHRDbContext _db;

    public PolicyDocumentService(NickHRDbContext db)
    {
        _db = db;
    }

    public async Task<PolicyDocumentDto> CreateAsync(string title, string category, string version, DateTime effectiveDate, string? description, string? filePath, bool requiresAcknowledgement)
    {
        var doc = new PolicyDocument
        {
            Title = title,
            Category = category,
            Version = version,
            EffectiveDate = effectiveDate,
            Description = description,
            FilePath = filePath,
            RequiresAcknowledgement = requiresAcknowledgement,
            IsActive = true
        };
        _db.PolicyDocuments.Add(doc);
        await _db.SaveChangesAsync();
        return await ProjectAsync(doc.Id);
    }

    public async Task<List<PolicyDocumentDto>> GetAllAsync()
    {
        var ids = await _db.PolicyDocuments
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => d.Id)
            .ToListAsync();
        var result = new List<PolicyDocumentDto>();
        foreach (var id in ids)
            result.Add(await ProjectAsync(id));
        return result;
    }

    public async Task<List<PolicyDocumentDto>> GetActiveAsync()
    {
        var ids = await _db.PolicyDocuments
            .Where(d => d.IsActive)
            .OrderBy(d => d.Category).ThenBy(d => d.Title)
            .Select(d => d.Id)
            .ToListAsync();
        var result = new List<PolicyDocumentDto>();
        foreach (var id in ids)
            result.Add(await ProjectAsync(id));
        return result;
    }

    public async Task<PolicyDocumentDto?> GetByIdAsync(int id)
    {
        var exists = await _db.PolicyDocuments.AnyAsync(d => d.Id == id && !d.IsDeleted);
        return exists ? await ProjectAsync(id) : null;
    }

    public async Task<PolicyDocumentDto> UpdateAsync(int id, string title, string category, string version, DateTime effectiveDate, string? description, string? filePath, bool requiresAcknowledgement, bool isActive)
    {
        var doc = await _db.PolicyDocuments.FirstOrDefaultAsync(d => d.Id == id && !d.IsDeleted)
            ?? throw new KeyNotFoundException($"Policy document {id} not found.");

        doc.Title = title;
        doc.Category = category;
        doc.Version = version;
        doc.EffectiveDate = effectiveDate;
        doc.Description = description;
        doc.FilePath = filePath;
        doc.RequiresAcknowledgement = requiresAcknowledgement;
        doc.IsActive = isActive;
        await _db.SaveChangesAsync();
        return await ProjectAsync(id);
    }

    public async Task AcknowledgeAsync(int policyDocumentId, int employeeId)
    {
        var exists = await _db.PolicyAcknowledgements
            .AnyAsync(a => a.PolicyDocumentId == policyDocumentId && a.EmployeeId == employeeId && !a.IsDeleted);
        if (exists) return;

        var ack = new PolicyAcknowledgement
        {
            PolicyDocumentId = policyDocumentId,
            EmployeeId = employeeId,
            AcknowledgedAt = DateTime.UtcNow
        };
        _db.PolicyAcknowledgements.Add(ack);
        await _db.SaveChangesAsync();
    }

    public async Task<List<PolicyAcknowledgementDto>> GetAcknowledgementStatusAsync(int policyDocumentId)
    {
        return await _db.PolicyAcknowledgements
            .Include(a => a.Employee)
            .Include(a => a.PolicyDocument)
            .Where(a => a.PolicyDocumentId == policyDocumentId && !a.IsDeleted)
            .OrderBy(a => a.AcknowledgedAt)
            .Select(a => new PolicyAcknowledgementDto(
                a.Id,
                a.PolicyDocumentId,
                a.PolicyDocument.Title,
                a.EmployeeId,
                a.Employee.FirstName + " " + a.Employee.LastName,
                a.AcknowledgedAt
            ))
            .ToListAsync();
    }

    public async Task<bool> HasAcknowledgedAsync(int policyDocumentId, int employeeId)
    {
        return await _db.PolicyAcknowledgements
            .AnyAsync(a => a.PolicyDocumentId == policyDocumentId && a.EmployeeId == employeeId && !a.IsDeleted);
    }

    private async Task<PolicyDocumentDto> ProjectAsync(int id)
    {
        var d = await _db.PolicyDocuments
            .Include(x => x.Acknowledgements)
            .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted)
            ?? throw new KeyNotFoundException($"Policy document {id} not found.");

        var totalEmployees = await _db.Employees.CountAsync(e => !e.IsDeleted);

        return new PolicyDocumentDto(
            d.Id,
            d.Title,
            d.Category,
            d.FilePath,
            d.Version,
            d.EffectiveDate,
            d.Description,
            d.IsActive,
            d.RequiresAcknowledgement,
            totalEmployees,
            d.Acknowledgements.Count(a => !a.IsDeleted),
            d.CreatedAt
        );
    }
}
