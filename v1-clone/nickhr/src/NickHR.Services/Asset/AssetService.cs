using Microsoft.EntityFrameworkCore;
using NickHR.Core.Entities.Core;
using NickHR.Core.Enums;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Asset;

public interface IAssetService
{
    Task<Core.Entities.Core.Asset> CreateAssetAsync(
        string tag, string name, AssetCategory category,
        string? description, string? serialNumber,
        DateTime? purchaseDate, decimal price, string condition);
    Task<List<Core.Entities.Core.Asset>> GetAllAssetsAsync(AssetCategory? category = null, AssetStatus? status = null);
    Task<AssetAssignment> AssignAssetAsync(int assetId, int employeeId, int assignedById, string? notes);
    Task<Core.Entities.Core.Asset> ReturnAssetAsync(int assetId, string condition, string? notes);
    Task<List<Core.Entities.Core.Asset>> GetEmployeeAssetsAsync(int employeeId);
    Task<List<AssetAssignment>> GetAssetHistoryAsync(int assetId);
}

public class AssetService : IAssetService
{
    private readonly NickHRDbContext _db;

    public AssetService(NickHRDbContext db)
    {
        _db = db;
    }

    public async Task<Core.Entities.Core.Asset> CreateAssetAsync(
        string tag, string name, AssetCategory category,
        string? description, string? serialNumber,
        DateTime? purchaseDate, decimal price, string condition)
    {
        var tagExists = await _db.Assets.AnyAsync(a => a.AssetTag == tag && !a.IsDeleted);
        if (tagExists)
            throw new InvalidOperationException($"Asset with tag '{tag}' already exists.");

        var asset = new Core.Entities.Core.Asset
        {
            AssetTag = tag,
            Name = name,
            Category = category,
            Description = description,
            SerialNumber = serialNumber,
            PurchaseDate = purchaseDate,
            PurchasePrice = price,
            Condition = condition,
            Status = AssetStatus.Available
        };

        _db.Assets.Add(asset);
        await _db.SaveChangesAsync();
        return asset;
    }

    public async Task<List<Core.Entities.Core.Asset>> GetAllAssetsAsync(
        AssetCategory? category = null, AssetStatus? status = null)
    {
        var query = _db.Assets
            .Include(a => a.AssignedToEmployee)
            .Where(a => !a.IsDeleted);

        if (category.HasValue)
            query = query.Where(a => a.Category == category.Value);
        if (status.HasValue)
            query = query.Where(a => a.Status == status.Value);

        return await query.OrderBy(a => a.Name).ToListAsync();
    }

    public async Task<AssetAssignment> AssignAssetAsync(
        int assetId, int employeeId, int assignedById, string? notes)
    {
        var asset = await _db.Assets.FindAsync(assetId)
            ?? throw new KeyNotFoundException($"Asset {assetId} not found.");

        if (asset.Status == AssetStatus.Assigned)
            throw new InvalidOperationException("Asset is already assigned to an employee.");

        if (asset.Status == AssetStatus.Retired || asset.Status == AssetStatus.Lost)
            throw new InvalidOperationException($"Asset cannot be assigned: status is {asset.Status}.");

        var employee = await _db.Employees.FindAsync(employeeId)
            ?? throw new KeyNotFoundException($"Employee {employeeId} not found.");

        var assignedBy = await _db.Employees.FindAsync(assignedById)
            ?? throw new KeyNotFoundException($"Assigning employee {assignedById} not found.");

        var now = DateTime.UtcNow;

        asset.AssignedToEmployeeId = employeeId;
        asset.AssignedDate = now;
        asset.ReturnDate = null;
        asset.Status = AssetStatus.Assigned;

        var assignment = new AssetAssignment
        {
            AssetId = assetId,
            EmployeeId = employeeId,
            AssignedById = assignedById,
            AssignedDate = now,
            Condition = asset.Condition,
            Notes = notes
        };

        _db.AssetAssignments.Add(assignment);
        await _db.SaveChangesAsync();
        return assignment;
    }

    public async Task<Core.Entities.Core.Asset> ReturnAssetAsync(int assetId, string condition, string? notes)
    {
        var asset = await _db.Assets.FindAsync(assetId)
            ?? throw new KeyNotFoundException($"Asset {assetId} not found.");

        if (asset.Status != AssetStatus.Assigned)
            throw new InvalidOperationException("Asset is not currently assigned.");

        var activeAssignment = await _db.AssetAssignments
            .Where(a => a.AssetId == assetId && a.ReturnedDate == null && !a.IsDeleted)
            .OrderByDescending(a => a.AssignedDate)
            .FirstOrDefaultAsync();

        if (activeAssignment != null)
        {
            activeAssignment.ReturnedDate = DateTime.UtcNow;
            activeAssignment.Condition = condition;
            if (!string.IsNullOrEmpty(notes))
                activeAssignment.Notes = notes;
        }

        asset.AssignedToEmployeeId = null;
        asset.ReturnDate = DateTime.UtcNow;
        asset.Condition = condition;
        asset.Status = AssetStatus.Available;

        await _db.SaveChangesAsync();
        return asset;
    }

    public async Task<List<Core.Entities.Core.Asset>> GetEmployeeAssetsAsync(int employeeId)
    {
        return await _db.Assets
            .Where(a => a.AssignedToEmployeeId == employeeId && !a.IsDeleted)
            .OrderBy(a => a.Category)
            .ToListAsync();
    }

    public async Task<List<AssetAssignment>> GetAssetHistoryAsync(int assetId)
    {
        return await _db.AssetAssignments
            .Include(a => a.Employee)
            .Include(a => a.AssignedBy)
            .Where(a => a.AssetId == assetId && !a.IsDeleted)
            .OrderByDescending(a => a.AssignedDate)
            .ToListAsync();
    }
}
