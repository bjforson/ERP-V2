namespace NickFinance.WebApp.Components.Shared;

/// <summary>
/// One physical site (Tema HQ, Kotoka, etc.) as surfaced to the picker.
/// The <see cref="SiteId"/> is a SHA-256-derived deterministic GUID so
/// every consumer (smoke runner, FloatNew page, future site registry)
/// agrees on the canonical id for "Tema". Once Track A.3 (Tenancy /
/// Sites) lands and a real <c>sites</c> table exists, this record stays
/// — only its source of truth changes from <see cref="SiteCatalog"/> to
/// the EF-backed query.
/// </summary>
public sealed record SiteEntry(Guid SiteId, string Name, string Code);

/// <summary>
/// Hardcoded list of the six Nick TC-Scan sites + their deterministic
/// GUIDs. Mirrors the `StableSiteGuid` helper that
/// <c>FloatNew.razor</c> + the smoke runner use, so a row created under
/// "Tema HQ" via the picker matches a row created via the Quick-Fill
/// stamp button — both resolve to the same GUID.
/// </summary>
public static class SiteCatalog
{
    private static readonly SiteEntry[] _sites = new[]
    {
        new SiteEntry(StableSiteGuid("Tema"),     "Tema HQ",   "TEMA"),
        new SiteEntry(StableSiteGuid("Kotoka"),   "Kotoka",    "KOTOKA"),
        new SiteEntry(StableSiteGuid("Takoradi"), "Takoradi",  "TAKORADI"),
        new SiteEntry(StableSiteGuid("Aflao"),    "Aflao",     "AFLAO"),
        new SiteEntry(StableSiteGuid("Paga"),     "Paga",      "PAGA"),
        new SiteEntry(StableSiteGuid("Elubo"),    "Elubo",     "ELUBO"),
    };

    public static IReadOnlyList<SiteEntry> All => _sites;

    /// <summary>
    /// Case-insensitive substring match on Name or Code. Empty query
    /// returns the full set (the picker shows the top 20 anyway).
    /// </summary>
    public static IReadOnlyList<SiteEntry> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return _sites;
        var q = query.Trim();
        return _sites
            .Where(s => s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || s.Code.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Resolve a site GUID back to its display entry. Returns null when
    /// the GUID isn't one of the six known sites — picker in that case
    /// falls back to the raw GUID display.
    /// </summary>
    public static SiteEntry? ById(Guid id) => _sites.FirstOrDefault(s => s.SiteId == id);

    /// <summary>
    /// Deterministic GUID from a site name. Mirrors <c>FloatNew.razor</c>'s
    /// <c>StableSiteGuid</c> so picker selection and Quick-Fill stamps
    /// converge on the same id.
    /// </summary>
    public static Guid StableSiteGuid(string name)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("nickerp-site:" + name.ToLowerInvariant());
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        var guidBytes = new byte[16];
        Array.Copy(hash, guidBytes, 16);
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x40);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes);
    }
}
