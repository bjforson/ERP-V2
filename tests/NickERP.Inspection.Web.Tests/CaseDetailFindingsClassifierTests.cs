using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Web.Services;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 32 FU-C — unit coverage for the static
/// <see cref="CaseDetailFindingsClassifier"/>. Covers the prefix-list
/// matching, the partition output, and the rule-id extraction shape the
/// CaseDetail.razor drill-down link relies on.
/// </summary>
public sealed class CaseDetailFindingsClassifierTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("validation.customsgh.port_match", true)]
    [InlineData("validation.fyco_direction", true)]
    [InlineData("VALIDATION.upper_case", true)]    // case-insensitive prefix match
    [InlineData("completeness.manifest_required", true)]
    [InlineData("Completeness.scanner_present", true)]
    [InlineData("analyst.annotation", false)]
    [InlineData("anomaly.organic", false)]          // legacy convention from Finding.cs xml-doc
    [InlineData("manifest.mismatch", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsSystemAuthored_MatchesByPrefix(string? findingType, bool expected)
    {
        CaseDetailFindingsClassifier.IsSystemAuthored(findingType).Should().Be(expected);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Partition_PreservesOrderWithinEachBucket()
    {
        var rid = Guid.NewGuid();
        var t0 = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var input = new[]
        {
            new Finding { AnalystReviewId = rid, FindingType = "validation.a", CreatedAt = t0 },
            new Finding { AnalystReviewId = rid, FindingType = "analyst.annotation", CreatedAt = t0.AddMinutes(1) },
            new Finding { AnalystReviewId = rid, FindingType = "validation.b", CreatedAt = t0.AddMinutes(2) },
            new Finding { AnalystReviewId = rid, FindingType = "completeness.c", CreatedAt = t0.AddMinutes(3) },
            new Finding { AnalystReviewId = rid, FindingType = "anomaly.organic", CreatedAt = t0.AddMinutes(4) },
        };

        var (validation, analyst) = CaseDetailFindingsClassifier.Partition(input);

        validation.Should().HaveCount(3);
        validation.Select(f => f.FindingType).Should().Equal("validation.a", "validation.b", "completeness.c");
        analyst.Should().HaveCount(2);
        analyst.Select(f => f.FindingType).Should().Equal("analyst.annotation", "anomaly.organic");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Partition_EmptyInput_ReturnsEmptyBuckets()
    {
        var (validation, analyst) = CaseDetailFindingsClassifier.Partition(Array.Empty<Finding>());
        validation.Should().BeEmpty();
        analyst.Should().BeEmpty();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("validation.customsgh.port_match", "customsgh.port_match")]
    [InlineData("completeness.manifest_required", "manifest_required")]
    [InlineData("VALIDATION.upper_case", "upper_case")] // case-insensitive prefix; tail case preserved
    [InlineData("analyst.annotation", null)]            // not a system prefix
    [InlineData("validation.", null)]                   // empty tail
    [InlineData("validation.   ", null)]                // whitespace tail
    [InlineData(null, null)]
    [InlineData("", null)]
    public void ExtractRuleId_StripsKnownPrefix(string? input, string? expected)
    {
        CaseDetailFindingsClassifier.ExtractRuleId(input).Should().Be(expected);
    }
}
