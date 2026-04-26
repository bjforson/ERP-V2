using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// A scanning lane / station within a <see cref="Location"/> — e.g.
/// "Tema Port / Lane 1". A station has zero or one currently-bound
/// scanner device at any time; the binding can rotate.
/// </summary>
public sealed class Station : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid LocationId { get; set; }
    public Location Location { get; set; } = null!;

    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public long TenantId { get; set; }
}
