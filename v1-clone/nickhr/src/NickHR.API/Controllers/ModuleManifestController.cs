using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace NickHR.API.Controllers;

/// <summary>
/// PLATFORM.md §6 platform-contract endpoint. Anonymous because the manifest
/// is identity-only metadata (no secrets) and the unified portal launcher
/// needs to read it before authentication.
/// </summary>
[AllowAnonymous]
[ApiController]
[Route("api/_module")]
public class ModuleManifestController : ControllerBase
{
    [HttpGet("manifest")]
    public IActionResult GetManifest() => Ok(new
    {
        name = "NickHR",
        displayName = "NickHR — HR Management",
        version = ThisAssemblyInformationalVersion(),
        description = "Employees, payroll, attendance, recruitment, performance.",
        platformContractVersion = "1.0",
        icon = "users",
        color = "#7c3aed",
        routes = new
        {
            home = "/dashboard",
            settings = "/admin/settings"
        },
        platformDependencies = new[] { "comms" },
        capabilities = new[]
        {
            "employee.manage",
            "payroll.run",
            "attendance.track",
            "recruitment.manage"
        },
        health = "/health"
    });

    private static string ThisAssemblyInformationalVersion()
    {
        var asm = typeof(ModuleManifestController).Assembly;
        var attr = (System.Reflection.AssemblyInformationalVersionAttribute?)Attribute.GetCustomAttribute(
            asm, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
        return attr?.InformationalVersion ?? asm.GetName().Version?.ToString() ?? "0.0.0";
    }
}
