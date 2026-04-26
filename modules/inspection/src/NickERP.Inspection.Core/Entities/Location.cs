using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// A physical site where inspection happens — e.g. "Tema Port",
/// "Kotoka Cargo Terminal". Users are assigned to locations; scanners
/// belong to stations within a location; external system bindings can
/// scope per-location.
/// </summary>
public sealed class Location : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Stable kebab-case code unique within tenant.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Human-readable name shown in admin UIs and lane signage.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional region grouping (e.g. "Greater Accra").</summary>
    public string? Region { get; set; }

    /// <summary>IANA timezone the location operates in. May differ from tenant default for multi-region tenants.</summary>
    public string TimeZone { get; set; } = "Africa/Accra";

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public long TenantId { get; set; }

    public List<Station> Stations { get; set; } = new();
}
