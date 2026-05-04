using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NickHR.Core.DTOs;
using NickHR.Core.Enums;
using NickHR.Core.Interfaces;
using NickHR.Services.Asset;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = RoleSets.HRStaff)]
public class AssetController : ControllerBase
{
    private readonly IAssetService _assetService;
    private readonly ICurrentUserService _currentUser;

    public AssetController(IAssetService assetService, ICurrentUserService currentUser)
    {
        _assetService = assetService;
        _currentUser = currentUser;
    }

    /// <summary>Create a new asset.</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAssetDto dto)
    {
        try
        {
            if (!Enum.TryParse<AssetCategory>(dto.Category, true, out var category))
                return BadRequest(ApiResponse<object>.Fail($"Invalid category: {dto.Category}."));

            var asset = await _assetService.CreateAssetAsync(
                dto.AssetTag, dto.Name, category,
                dto.Description, dto.SerialNumber,
                dto.PurchaseDate, dto.PurchasePrice, dto.Condition);

            return Ok(ApiResponse<object>.Ok(asset, "Asset created."));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Get all assets, optionally filtered by category and/or status.</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? category = null,
        [FromQuery] string? status = null)
    {
        AssetCategory? categoryEnum = null;
        AssetStatus? statusEnum = null;

        if (!string.IsNullOrEmpty(category) && !Enum.TryParse(category, true, out AssetCategory parsedCategory))
            return BadRequest(ApiResponse<object>.Fail($"Invalid category: {category}."));
        else if (!string.IsNullOrEmpty(category))
            categoryEnum = Enum.Parse<AssetCategory>(category, true);

        if (!string.IsNullOrEmpty(status) && !Enum.TryParse(status, true, out AssetStatus parsedStatus))
            return BadRequest(ApiResponse<object>.Fail($"Invalid status: {status}."));
        else if (!string.IsNullOrEmpty(status))
            statusEnum = Enum.Parse<AssetStatus>(status, true);

        var assets = await _assetService.GetAllAssetsAsync(categoryEnum, statusEnum);
        return Ok(ApiResponse<object>.Ok(assets));
    }

    /// <summary>Assign an asset to an employee.</summary>
    [HttpPost("{assetId:int}/assign")]
    public async Task<IActionResult> Assign(int assetId, [FromBody] AssignAssetDto dto)
    {
        try
        {
            var assignedById = _currentUser.EmployeeId
                ?? throw new InvalidOperationException("No employee profile linked to current user.");

            var assignment = await _assetService.AssignAssetAsync(assetId, dto.EmployeeId, assignedById, dto.Notes);
            return Ok(ApiResponse<object>.Ok(assignment, "Asset assigned."));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Return an assigned asset.</summary>
    [HttpPost("{assetId:int}/return")]
    public async Task<IActionResult> Return(int assetId, [FromBody] ReturnAssetDto dto)
    {
        try
        {
            var asset = await _assetService.ReturnAssetAsync(assetId, dto.Condition, dto.Notes);
            return Ok(ApiResponse<object>.Ok(asset, "Asset returned."));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    /// <summary>Get assets assigned to a specific employee.</summary>
    [HttpGet("employee/{employeeId:int}")]
    [Authorize]
    public async Task<IActionResult> GetEmployeeAssets(int employeeId)
    {
        var assets = await _assetService.GetEmployeeAssetsAsync(employeeId);
        return Ok(ApiResponse<object>.Ok(assets));
    }

    /// <summary>Get assignment history for an asset.</summary>
    [HttpGet("{assetId:int}/history")]
    public async Task<IActionResult> GetHistory(int assetId)
    {
        var history = await _assetService.GetAssetHistoryAsync(assetId);
        return Ok(ApiResponse<object>.Ok(history));
    }

    /// <summary>Get my assigned assets.</summary>
    [HttpGet("my")]
    [Authorize]
    public async Task<IActionResult> GetMyAssets()
    {
        var employeeId = _currentUser.EmployeeId;
        if (employeeId == null) return Unauthorized();

        var assets = await _assetService.GetEmployeeAssetsAsync(employeeId.Value);
        return Ok(ApiResponse<object>.Ok(assets));
    }
}

public class CreateAssetDto
{
    public string AssetTag { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? SerialNumber { get; set; }
    public DateTime? PurchaseDate { get; set; }
    public decimal PurchasePrice { get; set; }
    public string Condition { get; set; } = "New";
}

public class AssignAssetDto
{
    public int EmployeeId { get; set; }
    public string? Notes { get; set; }
}

public class ReturnAssetDto
{
    public string Condition { get; set; } = string.Empty;
    public string? Notes { get; set; }
}
