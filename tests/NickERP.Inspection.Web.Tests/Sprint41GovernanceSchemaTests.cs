using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using Xunit;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 44 / Phase C — smoke tests for the Sprint 41 governance
/// schema fix. The 3 entities (ScannerOnboardingResponse +
/// ThresholdProfileHistory + WebhookCursor) shipped in Sprint 41
/// without backing tables; Phase C added the
/// <c>Add_Sprint41_GovernanceTables</c> migration with proper
/// CreateTable DDL. These tests confirm the DbContext + DbSets +
/// model snapshot agree on the entity shape under the in-memory
/// provider — i.e. SaveChangesAsync round-trips don't throw a
/// "no DbSet for type" or "missing key" exception.
/// </summary>
public sealed class Sprint41GovernanceSchemaTests : IDisposable
{
    private readonly ServiceProvider _sp;

    public Sprint41GovernanceSchemaTests()
    {
        var dbName = "s44-sprint41-schema-" + Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        _sp = services.BuildServiceProvider();
    }

    public void Dispose() => _sp.Dispose();

    [Fact]
    public async Task ScannerOnboardingResponse_round_trips()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();

        var row = new ScannerOnboardingResponse
        {
            Id = Guid.NewGuid(),
            ScannerDeviceTypeId = "fs6000",
            FieldName = "manufacturer_model",
            Value = "FS6000-Mk2",
            RecordedAt = DateTimeOffset.UtcNow,
            RecordedByUserId = Guid.NewGuid(),
            TenantId = 1
        };
        db.ScannerOnboardingResponses.Add(row);
        await db.SaveChangesAsync();

        var got = await db.ScannerOnboardingResponses.AsNoTracking().FirstAsync();
        Assert.Equal("fs6000", got.ScannerDeviceTypeId);
        Assert.Equal("manufacturer_model", got.FieldName);
        Assert.Equal("FS6000-Mk2", got.Value);
    }

    [Fact]
    public async Task ThresholdProfileHistory_round_trips()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();

        var row = new ThresholdProfileHistory
        {
            Id = Guid.NewGuid(),
            ScannerDeviceInstanceId = Guid.NewGuid(),
            ModelId = "fs6000.material_anomaly",
            ClassId = "weapon",
            OldThreshold = 0.40,
            NewThreshold = 0.55,
            ChangedAt = DateTimeOffset.UtcNow,
            ChangedByUserId = Guid.NewGuid(),
            Reason = "manual tune after FP cluster on 04-29",
            TenantId = 1
        };
        db.ThresholdProfileHistory.Add(row);
        await db.SaveChangesAsync();

        var got = await db.ThresholdProfileHistory.AsNoTracking().FirstAsync();
        Assert.Equal(0.40, got.OldThreshold);
        Assert.Equal(0.55, got.NewThreshold);
        Assert.Equal("weapon", got.ClassId);
    }

    [Fact]
    public async Task ThresholdProfileHistory_OldThreshold_can_be_null_for_bootstrap_row()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();

        var row = new ThresholdProfileHistory
        {
            Id = Guid.NewGuid(),
            ScannerDeviceInstanceId = Guid.NewGuid(),
            ModelId = "fs6000.material_anomaly",
            ClassId = "weapon",
            OldThreshold = null, // bootstrap / first proposal — no prior Active.
            NewThreshold = 0.50,
            ChangedAt = DateTimeOffset.UtcNow,
            TenantId = 1
        };
        db.ThresholdProfileHistory.Add(row);
        await db.SaveChangesAsync();

        var got = await db.ThresholdProfileHistory.AsNoTracking().FirstAsync();
        Assert.Null(got.OldThreshold);
        Assert.Equal(0.50, got.NewThreshold);
    }

    [Fact]
    public async Task WebhookCursor_round_trips()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();

        var row = new WebhookCursor
        {
            Id = Guid.NewGuid(),
            AdapterName = "siem.forwarder",
            LastProcessedEventId = Guid.NewGuid(),
            UpdatedAt = DateTimeOffset.UtcNow,
            TenantId = 1
        };
        db.WebhookCursors.Add(row);
        await db.SaveChangesAsync();

        var got = await db.WebhookCursors.AsNoTracking().FirstAsync();
        Assert.Equal("siem.forwarder", got.AdapterName);
        Assert.NotEqual(Guid.Empty, got.LastProcessedEventId);
    }

    [Fact]
    public async Task WebhookCursor_LastProcessedEventId_starts_as_Empty_sentinel()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();

        var row = new WebhookCursor
        {
            Id = Guid.NewGuid(),
            AdapterName = "fresh.adapter",
            LastProcessedEventId = Guid.Empty, // sentinel — read from start of stream.
            UpdatedAt = DateTimeOffset.UtcNow,
            TenantId = 1
        };
        db.WebhookCursors.Add(row);
        await db.SaveChangesAsync();

        var got = await db.WebhookCursors.AsNoTracking().FirstAsync();
        Assert.Equal(Guid.Empty, got.LastProcessedEventId);
    }

    [Fact]
    public void DbSets_are_exposed_for_all_three_governance_entities()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        Assert.NotNull(db.ScannerOnboardingResponses);
        Assert.NotNull(db.ThresholdProfileHistory);
        Assert.NotNull(db.WebhookCursors);
    }

    [Fact]
    public async Task ScannerOnboardingResponses_supports_multiple_history_rows_per_field()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();

        var ts0 = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);
        var ts1 = ts0.AddDays(7);
        // First answer.
        db.ScannerOnboardingResponses.Add(new ScannerOnboardingResponse
        {
            Id = Guid.NewGuid(),
            ScannerDeviceTypeId = "fs6000",
            FieldName = "image_export_format",
            Value = "TIFF",
            RecordedAt = ts0,
            TenantId = 1
        });
        // Re-recorded a week later.
        db.ScannerOnboardingResponses.Add(new ScannerOnboardingResponse
        {
            Id = Guid.NewGuid(),
            ScannerDeviceTypeId = "fs6000",
            FieldName = "image_export_format",
            Value = "TIFF + JPEG",
            RecordedAt = ts1,
            TenantId = 1
        });
        await db.SaveChangesAsync();

        var rows = await db.ScannerOnboardingResponses
            .AsNoTracking()
            .Where(r => r.ScannerDeviceTypeId == "fs6000" && r.FieldName == "image_export_format")
            .ToListAsync();
        Assert.Equal(2, rows.Count);
    }
}
