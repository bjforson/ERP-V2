namespace NickERP.Perf.Tests;

/// <summary>
/// Load profile multipliers per docs/perf/test-plan.md §1 + §3.
/// </summary>
public enum LoadProfile
{
    /// <summary>Pilot peak (Kotoka / Takoradi). Acceptance gates enforce.</summary>
    Pilot1x,

    /// <summary>Tema-shaped projection. Relaxed gates.</summary>
    Tema5x,

    /// <summary>Stress / breaking-point discovery. Informative only.</summary>
    Stress10x,
}
