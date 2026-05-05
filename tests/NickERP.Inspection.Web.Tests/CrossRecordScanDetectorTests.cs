using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Application.Detection;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 31 / B5.2 Phase D — coverage for the
/// <see cref="CrossRecordScanDetector"/>. Asserts the multi-document
/// fan-out and scan-metadata multi-token signals fire correctly +
/// the single-subject case stays clean.
/// </summary>
public sealed class CrossRecordScanDetectorTests : IDisposable
{
    private readonly InspectionDbContext _db;

    public CrossRecordScanDetectorTests()
    {
        var options = new DbContextOptionsBuilder<InspectionDbContext>()
            .UseInMemoryDatabase("crs-detector-" + Guid.NewGuid())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new InspectionDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    private CrossRecordScanDetector NewDetector()
        => new(_db, NullLogger<CrossRecordScanDetector>.Instance);

    private async Task<InspectionCase> SeedCaseAsync(string subject)
    {
        var c = new InspectionCase
        {
            Id = Guid.NewGuid(),
            LocationId = Guid.NewGuid(),
            SubjectIdentifier = subject,
            State = InspectionWorkflowState.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            StateEnteredAt = DateTimeOffset.UtcNow,
            TenantId = 1
        };
        _db.Cases.Add(c);
        await _db.SaveChangesAsync();
        return c;
    }

    [Fact]
    public async Task Returns_null_for_unknown_case()
    {
        var detector = NewDetector();
        var result = await detector.DetectAsync(Guid.NewGuid());
        result.Should().BeNull();
    }

    [Fact]
    public async Task Single_subject_case_returns_null()
    {
        var c = await SeedCaseAsync("CONT001");
        // Add a doc whose containerNumber matches the case subject — no
        // signal.
        _db.AuthorityDocuments.Add(new AuthorityDocument
        {
            Id = Guid.NewGuid(), CaseId = c.Id, ExternalSystemInstanceId = Guid.NewGuid(),
            DocumentType = "BOE", ReferenceNumber = "ref-1",
            PayloadJson = "{\"containerNumber\":\"CONT001\"}",
            ReceivedAt = DateTimeOffset.UtcNow, TenantId = 1
        });
        await _db.SaveChangesAsync();

        var result = await NewDetector().DetectAsync(c.Id);
        result.Should().BeNull();
    }

    [Fact]
    public async Task Multi_document_fan_out_fires_signal()
    {
        var c = await SeedCaseAsync("CONT001");
        // Two docs with different container numbers — multi-subject hint.
        _db.AuthorityDocuments.AddRange(
            new AuthorityDocument
            {
                Id = Guid.NewGuid(), CaseId = c.Id, ExternalSystemInstanceId = Guid.NewGuid(),
                DocumentType = "BOE", ReferenceNumber = "ref-1",
                PayloadJson = "{\"containerNumber\":\"CONT001\"}",
                ReceivedAt = DateTimeOffset.UtcNow, TenantId = 1
            },
            new AuthorityDocument
            {
                Id = Guid.NewGuid(), CaseId = c.Id, ExternalSystemInstanceId = Guid.NewGuid(),
                DocumentType = "BOE", ReferenceNumber = "ref-2",
                PayloadJson = "{\"containerNumber\":\"CONT999\"}",
                ReceivedAt = DateTimeOffset.UtcNow, TenantId = 1
            });
        await _db.SaveChangesAsync();

        var result = await NewDetector().DetectAsync(c.Id);
        result.Should().NotBeNull();
        result!.Subjects.Select(s => s.SubjectIdentifier)
            .Should().BeEquivalentTo(new[] { "CONT001", "CONT999" });
    }

    [Fact]
    public async Task Scan_metadata_multi_token_fires_signal()
    {
        var c = await SeedCaseAsync("CONT001");
        var scan = new Scan
        {
            Id = Guid.NewGuid(), CaseId = c.Id,
            ScannerDeviceInstanceId = Guid.NewGuid(),
            CapturedAt = DateTimeOffset.UtcNow,
            IdempotencyKey = "k", TenantId = 1
        };
        _db.Scans.Add(scan);
        _db.ScanArtifacts.Add(new ScanArtifact
        {
            Id = Guid.NewGuid(), ScanId = scan.Id,
            ArtifactKind = "Primary", StorageUri = "noop://", MimeType = "image/png",
            ContentHash = "abc",
            MetadataJson = "{\"containerNumbers\":[\"CONT001\",\"CONT222\",\"CONT333\"]}",
            CreatedAt = DateTimeOffset.UtcNow, TenantId = 1
        });
        await _db.SaveChangesAsync();

        var result = await NewDetector().DetectAsync(c.Id);
        result.Should().NotBeNull();
        result!.Subjects.Select(s => s.SubjectIdentifier)
            .Should().Contain(new[] { "CONT001", "CONT222", "CONT333" });
    }

    [Fact]
    public async Task Detector_version_is_stable()
    {
        var detector = NewDetector();
        detector.DetectorVersion.Should().Be("v1");
    }

    [Fact]
    public async Task Both_signals_combined_dedupe()
    {
        var c = await SeedCaseAsync("CONT001");
        // Doc and metadata both reference CONT222 — dedupe to one
        // entry in the descriptor's subject list (besides the
        // primary).
        _db.AuthorityDocuments.Add(new AuthorityDocument
        {
            Id = Guid.NewGuid(), CaseId = c.Id, ExternalSystemInstanceId = Guid.NewGuid(),
            DocumentType = "BOE", ReferenceNumber = "ref-1",
            PayloadJson = "{\"containerNumber\":\"CONT222\"}",
            ReceivedAt = DateTimeOffset.UtcNow, TenantId = 1
        });
        var scan = new Scan
        {
            Id = Guid.NewGuid(), CaseId = c.Id,
            ScannerDeviceInstanceId = Guid.NewGuid(),
            CapturedAt = DateTimeOffset.UtcNow,
            IdempotencyKey = "k", TenantId = 1
        };
        _db.Scans.Add(scan);
        _db.ScanArtifacts.Add(new ScanArtifact
        {
            Id = Guid.NewGuid(), ScanId = scan.Id,
            ArtifactKind = "Primary", StorageUri = "noop://", MimeType = "image/png",
            ContentHash = "abc",
            MetadataJson = "{\"containerNumbers\":[\"CONT222\"]}",
            CreatedAt = DateTimeOffset.UtcNow, TenantId = 1
        });
        await _db.SaveChangesAsync();

        var result = await NewDetector().DetectAsync(c.Id);
        result.Should().NotBeNull();
        // Primary CONT001 + the CONT222 from both signals (deduped).
        result!.Subjects.Where(s => s.SubjectIdentifier == "CONT222")
            .Should().HaveCount(1);
    }
}
