namespace NickERP.EdgeNode;

/// <summary>
/// Sprint 11 / P2 — runtime configuration for the edge node host.
/// Bound from the <c>EdgeNode</c> section in <c>appsettings.json</c>
/// + env-var overrides (the hosting default).
/// </summary>
public sealed class EdgeNodeOptions
{
    /// <summary>
    /// Stable id for this edge node, e.g. <c>edge-tema-1</c>. Used by
    /// the server-side <c>edge_node_authorizations</c> check to gate
    /// per-tenant replay. MUST be unique across the suite — a clash
    /// silently bypasses authorization.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Shared-secret token presented to the server in the
    /// <c>X-Edge-Token</c> header on every replay request. Matches the
    /// server-side <c>EdgeNode:SharedSecret</c>. Document as a TODO —
    /// proper edge auth (mTLS / per-edge JWTs) lands in a follow-up.
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// How often the worker probes the server + drains the buffer.
    /// Default 30s. Tune lower in environments with spotty links so
    /// reconnection is observed quickly; tune higher in steady-state
    /// connected environments to spare network.
    /// </summary>
    public int ReplayIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum number of buffered events to send in a single replay
    /// batch. Default 50 — keeps a single HTTP roundtrip under a few
    /// hundred KB for typical audit-event payloads.
    /// </summary>
    public int MaxBatchSize { get; set; } = 50;
}

/// <summary>
/// Server-connection settings — broken out so the edge can re-target
/// at deploy without touching the <see cref="EdgeNodeOptions"/> shape.
/// </summary>
public sealed class EdgeServerOptions
{
    /// <summary>
    /// Base URL of the central NickERP server, e.g. <c>https://nickerp.example.com</c>.
    /// The worker probes <c>{Url}/healthz/ready</c> and posts to
    /// <c>{Url}/api/edge/replay</c>.
    /// </summary>
    public string Url { get; set; } = string.Empty;
}
