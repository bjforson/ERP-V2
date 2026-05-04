using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NickHR.Core.DTOs;
using NickHR.Services.Payroll.EFiling;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/payroll/efile")]
[Authorize(Roles = RoleSets.SeniorHROrPayroll)]
public class EFilingController : ControllerBase
{
    private readonly EFilingService _eFilingService;

    public EFilingController(EFilingService eFilingService)
    {
        _eFilingService = eFilingService;
    }

    [HttpGet("ssnit")]
    public async Task<IActionResult> DownloadSSNITEFile([FromQuery] int month, [FromQuery] int year)
    {
        try
        {
            var bytes = await _eFilingService.GenerateSSNITEFileAsync(month, year);
            return File(bytes, "text/csv", $"SSNIT_EFile_{year}_{month:D2}.csv");
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    [HttpGet("gra-paye")]
    public async Task<IActionResult> DownloadGRAEFile([FromQuery] int month, [FromQuery] int year)
    {
        try
        {
            var bytes = await _eFilingService.GenerateGRAEFileAsync(month, year);
            return File(bytes, "text/csv", $"GRA_PAYE_{year}_{month:D2}.csv");
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }

    [HttpGet("gra-annual")]
    public async Task<IActionResult> DownloadGRAAnnualEFile([FromQuery] int year)
    {
        try
        {
            var bytes = await _eFilingService.GenerateGRAAnnualEFileAsync(year);
            return File(bytes, "text/csv", $"GRA_Annual_Return_{year}.csv");
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object>.Fail(ex.Message));
        }
    }
}
