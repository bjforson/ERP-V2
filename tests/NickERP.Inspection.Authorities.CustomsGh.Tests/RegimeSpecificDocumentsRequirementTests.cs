using System.Text.Json;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Authorities.CustomsGh;
using NickERP.Inspection.Authorities.CustomsGh.Completeness;
using NickERP.Inspection.Core.Completeness;
using NickERP.Inspection.Core.Entities;

namespace NickERP.Inspection.Authorities.CustomsGh.Tests;

/// <summary>
/// Sprint 48 / Phase C — coverage for
/// <see cref="RegimeSpecificDocumentsRequirement"/>. Asserts the regime →
/// expected-document mapping (40/70/90 → BOE; 80 → Manifest/CMR; other
/// regimes → Skip).
/// </summary>
public sealed class RegimeSpecificDocumentsRequirementTests
{
    private static RegimeSpecificDocumentsRequirement BuildRequirement()
        => new(Options.Create(new CustomsGhValidationOptions()));

    private static AuthorityDocument BuildDoc(
        string regimeCode,
        string docType,
        string clearanceType = "IM")
    {
        return new AuthorityDocument
        {
            Id = Guid.NewGuid(),
            CaseId = Guid.NewGuid(),
            ExternalSystemInstanceId = Guid.NewGuid(),
            DocumentType = docType,
            ReferenceNumber = "ref-" + Guid.NewGuid().ToString("N")[..8],
            PayloadJson = JsonSerializer.Serialize(new
            {
                Header = new { ClearanceType = clearanceType, RegimeCode = regimeCode },
                ManifestDetails = new { DeliveryPlace = "WTTMA1MPS3" }
            }),
            ReceivedAt = DateTimeOffset.UtcNow,
            TenantId = 1
        };
    }

    private static CompletenessContext BuildContext(IReadOnlyList<AuthorityDocument> docs)
    {
        return new CompletenessContext(
            Case: new InspectionCase { Id = Guid.NewGuid(), TenantId = 1 },
            Scans: Array.Empty<Scan>(),
            ScanArtifacts: Array.Empty<ScanArtifact>(),
            Documents: docs,
            AnalystReviews: Array.Empty<AnalystReview>(),
            Verdicts: Array.Empty<Verdict>(),
            TenantId: 1);
    }

    [Theory]
    [InlineData("40")]
    [InlineData("70")]
    [InlineData("90")]
    public void Import_or_export_regime_with_BOE_passes(string regime)
    {
        var req = BuildRequirement();
        var ctx = BuildContext(new[] { BuildDoc(regimeCode: regime, docType: "BOE") });

        var outcome = req.Evaluate(ctx);

        outcome.Severity.Should().Be(CompletenessSeverity.Pass);
    }

    [Theory]
    [InlineData("40")]
    [InlineData("70")]
    [InlineData("90")]
    public void Import_or_export_regime_without_BOE_is_Incomplete(string regime)
    {
        // Manifest only (no BOE) for a non-transit regime — Incomplete.
        var req = BuildRequirement();
        var ctx = BuildContext(new[] { BuildDoc(regimeCode: regime, docType: "Manifest") });

        var outcome = req.Evaluate(ctx);

        outcome.Severity.Should().Be(CompletenessSeverity.Incomplete);
        outcome.MissingFields.Should().NotBeNull();
        outcome.MissingFields.Should().ContainSingle()
            .Which.Should().Be($"regime-{regime}-boe");
    }

    [Fact]
    public void Transit_regime_with_Manifest_passes()
    {
        // Regime 80 = transit; Manifest is the matching companion type.
        var req = BuildRequirement();
        var ctx = BuildContext(new[] { BuildDoc(regimeCode: "80", docType: "Manifest") });

        var outcome = req.Evaluate(ctx);

        outcome.Severity.Should().Be(CompletenessSeverity.Pass);
    }

    [Fact]
    public void Transit_regime_with_CMR_passes()
    {
        // Regime 80 with a CMR document also passes (CMR is the
        // alternate transit-document shape).
        var req = BuildRequirement();
        var ctx = BuildContext(new[] { BuildDoc(regimeCode: "80", docType: "CMR") });

        var outcome = req.Evaluate(ctx);

        outcome.Severity.Should().Be(CompletenessSeverity.Pass);
    }

    [Fact]
    public void Transit_regime_without_Manifest_or_CMR_is_Incomplete()
    {
        // Regime 80 with only a BOE — Incomplete (transit needs Manifest
        // / CMR).
        var req = BuildRequirement();
        var ctx = BuildContext(new[] { BuildDoc(regimeCode: "80", docType: "BOE") });

        var outcome = req.Evaluate(ctx);

        outcome.Severity.Should().Be(CompletenessSeverity.Incomplete);
        outcome.MissingFields.Should().ContainSingle()
            .Which.Should().Be("regime-80-manifest-or-cmr");
    }

    [Theory]
    [InlineData("10")]
    [InlineData("19")]
    [InlineData("50")]
    [InlineData("60")]
    public void Out_of_set_regimes_skip(string regime)
    {
        var req = BuildRequirement();
        var ctx = BuildContext(new[] { BuildDoc(regimeCode: regime, docType: "BOE") });

        var outcome = req.Evaluate(ctx);

        outcome.Severity.Should().Be(CompletenessSeverity.Skip,
            because: "regime is outside the gated 40/70/80/90 set");
    }

    [Fact]
    public void Empty_documents_skips()
    {
        var req = BuildRequirement();
        var ctx = BuildContext(Array.Empty<AuthorityDocument>());

        var outcome = req.Evaluate(ctx);

        outcome.Severity.Should().Be(CompletenessSeverity.Skip);
    }

    [Fact]
    public void Half_state_only_documents_skip()
    {
        // Blank regime case: out-of-scope — half-state CMR coverage is
        // CmrPortStateRequirement's job, not this rule's.
        var req = BuildRequirement();
        var ctx = BuildContext(new[]
        {
            BuildDoc(regimeCode: "", docType: "CMR")
        });

        var outcome = req.Evaluate(ctx);

        outcome.Severity.Should().Be(CompletenessSeverity.Skip);
    }
}
