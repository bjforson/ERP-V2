using System.ComponentModel.DataAnnotations;
using NickERP.Platform.Identity.Api.Models;

namespace NickERP.Platform.Tests;

/// <summary>
/// G1 #6 — <c>POST /api/identity/scopes</c> must reject underspecified
/// scope codes at the API boundary. The DataAnnotations regex is the
/// guard; this exercises it directly without spinning up the host.
/// </summary>
public class AppScopeCodeRegexTests
{
    [Theory]
    [InlineData("Identity.Admin")]
    [InlineData("Inspection.CaseReviewer")]
    [InlineData("Finance.PettyCash.Approver")]
    [InlineData("Finance.Reports.Read")]
    [InlineData("Finance.Petty.Cash.Approver.Lead")]
    public void Valid_scope_codes_pass_validation(string code)
    {
        var req = new CreateAppScopeRequest { Code = code, AppName = "test", TenantId = 1 };
        TryValidate(req, out var problem).Should().BeTrue($"'{code}' should be accepted: {string.Join(';', problem.Values.SelectMany(x => x))}");
    }

    [Theory]
    [InlineData("admin")]            // single segment, lowercase
    [InlineData("Admin")]            // single segment, capitalised
    [InlineData("admin.foo")]        // lowercase first segment
    [InlineData("Finance.foo")]      // lowercase second segment
    [InlineData("Finance.123Approver")] // digit in segment
    [InlineData("Finance.Petty_Cash")]  // underscore
    [InlineData("Finance.Petty-Cash")]  // dash
    [InlineData("Finance.A.B")]      // segment too short (single letter)
    [InlineData("Finance..Approver")] // empty segment
    [InlineData(".Finance.Approver")] // leading dot
    [InlineData("Finance.Approver.")] // trailing dot
    public void Invalid_scope_codes_fail_validation(string code)
    {
        var req = new CreateAppScopeRequest { Code = code, AppName = "test", TenantId = 1 };
        TryValidate(req, out _).Should().BeFalse($"'{code}' should be rejected.");
    }

    private static bool TryValidate(object instance, out IDictionary<string, string[]> problem)
    {
        var ctx = new ValidationContext(instance, null, null);
        var errors = new List<ValidationResult>();
        var ok = Validator.TryValidateObject(instance, ctx, errors, validateAllProperties: true);
        problem = errors
            .SelectMany(e => (e.MemberNames.Any() ? e.MemberNames : new[] { string.Empty })
                .Select(m => new { Member = m, Message = e.ErrorMessage ?? "Invalid." }))
            .GroupBy(x => x.Member)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Message).ToArray());
        return ok;
    }
}
