using System.Text.Json;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Authorities.CustomsGh;
using NickERP.Inspection.Authorities.CustomsGh.Completeness;
using NickERP.Inspection.Core.Completeness;
using NickERP.Inspection.Core.Entities;

namespace NickERP.Inspection.Authorities.CustomsGh.Tests;

/// <summary>
/// Sprint 48 / Phase C — coverage for <see cref="CmrPortStateRequirement"/>.
/// Half-state CMR cases must carry both port-of-loading + port-of-discharge
/// until the BOE/IM lands; non-blank-regime cases skip the check.
/// </summary>
public sealed class CmrPortStateRequirementTests
{
    private static CmrPortStateRequirement BuildRequirement()
        => new(Options.Create(new CustomsGhValidationOptions()));

    private static AuthorityDocument BuildDoc(
        string clearanceType,
        string regimeCode,
        string? portOfLoading = null,
        string? portOfDischarge = null,
        string docType = "BOE")
    {
        return new AuthorityDocument
        {
            Id = Guid.NewGuid(),
            CaseId = Guid.NewGuid(),
            ExternalSystemInstanceId = Guid.NewGuid(),
            DocumentType = docType,
            ReferenceNumber = "ref-1",
            PayloadJson = JsonSerializer.Serialize(new
            {
                Header = new { ClearanceType = clearanceType, RegimeCode = regimeCode },
                ManifestDetails = new
                {
                    DeliveryPlace = "WTTMA1MPS3",
                    PortOfLoading = portOfLoading,
                    PortOfDischarge = portOfDischarge
                }
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

    [Fact]
    public void HalfState_with_missing_ports_is_Incomplete()
    {
        var req = BuildRequirement();
        var ctx = BuildContext(new[]
        {
            BuildDoc(clearanceType: "CMR", regimeCode: "", docType: "CMR")
        });

        var outcome = req.Evaluate(ctx);

        outcome.Severity.Should().Be(CompletenessSeverity.Incomplete);
        outcome.MissingFields.Should().NotBeNull();
        outcome.MissingFields.Should().Contain("port-of-loading");
        outcome.MissingFields.Should().Contain("port-of-discharge");
    }

    [Fact]
    public void HalfState_with_both_ports_is_Pass()
    {
        var req = BuildRequirement();
        var ctx = BuildContext(new[]
        {
            BuildDoc(
                clearanceType: "CMR",
                regimeCode: "",
                portOfLoading: "CNYTN",
                portOfDischarge: "GHTKD",
                docType: "CMR")
        });

        var outcome = req.Evaluate(ctx);

        outcome.Severity.Should().Be(CompletenessSeverity.Pass);
    }

    [Fact]
    public void NonBlank_regime_skips()
    {
        // Once the regime is classified (non-blank, non-CMR), the
        // half-state checks no longer apply.
        var req = BuildRequirement();
        var ctx = BuildContext(new[]
        {
            BuildDoc(clearanceType: "IM", regimeCode: "40", docType: "BOE")
        });

        var outcome = req.Evaluate(ctx);

        outcome.Severity.Should().Be(CompletenessSeverity.Skip);
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
    public void Mixed_halfstate_and_classified_docs_skips()
    {
        // If even one document carries a classified regime, the
        // requirement skips for the case — the case has progressed
        // past the half-state phase.
        var req = BuildRequirement();
        var ctx = BuildContext(new[]
        {
            BuildDoc(clearanceType: "CMR", regimeCode: "", docType: "CMR"),
            BuildDoc(clearanceType: "IM", regimeCode: "40", docType: "BOE")
        });

        var outcome = req.Evaluate(ctx);

        outcome.Severity.Should().Be(CompletenessSeverity.Skip);
    }

    [Fact]
    public void HalfState_with_only_pol_missing_is_Incomplete()
    {
        var req = BuildRequirement();
        var ctx = BuildContext(new[]
        {
            BuildDoc(
                clearanceType: "CMR",
                regimeCode: "",
                portOfLoading: null,
                portOfDischarge: "GHTKD",
                docType: "CMR")
        });

        var outcome = req.Evaluate(ctx);

        outcome.Severity.Should().Be(CompletenessSeverity.Incomplete);
        outcome.MissingFields.Should().BeEquivalentTo(new[] { "port-of-loading" });
    }
}
