using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// A physical scanner unit. Owned by a <see cref="Location"/> (so it can't
/// accidentally be reassigned cross-location). The <see cref="TypeCode"/>
/// matches the <c>[Plugin(typeCode)]</c> on a registered
/// <c>IScannerAdapter</c> implementation; <see cref="ConfigJson"/> is the
/// instance-level config validated against that plugin's manifest schema.
/// </summary>
public sealed class ScannerDeviceInstance : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid LocationId { get; set; }
    public Location? Location { get; set; }

    /// <summary>Optional current binding to a <see cref="Station"/>; null = device not in service.</summary>
    public Guid? StationId { get; set; }
    public Station? Station { get; set; }

    /// <summary>Plugin type code, matches an <c>IScannerAdapter</c>'s <c>[Plugin]</c>. E.g. "fs6000", "ase", "mock".</summary>
    public string TypeCode { get; set; } = string.Empty;

    /// <summary>Display label — usually serial / asset tag.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Free-form description.</summary>
    public string? Description { get; set; }

    /// <summary>Instance config (JSON). Schema is the plugin's <c>configSchema</c>; validated at admin time.</summary>
    public string ConfigJson { get; set; } = "{}";

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public long TenantId { get; set; }
}
