using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NickERP.EdgeNode;

/// <summary>
/// Sprint 11 / P2 — abstracts the HTTP calls the
/// <see cref="EdgeReplayWorker"/> makes against the central server.
/// Pulled into an interface so unit tests can drive the worker without
/// spinning up a real <see cref="HttpClient"/> + WireMock pair.
/// </summary>
public interface IEdgeReplayClient
{
    /// <summary>
    /// Probe <c>{server}/healthz/ready</c> with a short timeout. Returns
    /// <c>true</c> when the server reports healthy. Any non-2xx /
    /// network failure / timeout returns <c>false</c> — the worker
    /// silently skips this tick.
    /// </summary>
    Task<bool> IsServerReachableAsync(CancellationToken ct);

    /// <summary>
    /// POST a batch of buffered entries to <c>{server}/api/edge/replay</c>.
    /// Returns the per-entry results from the server (same length as
    /// the input). Throws on transport-level failure (5xx, timeout) so
    /// the worker can treat the whole batch as transient.
    /// </summary>
    Task<EdgeReplayResponse> SendBatchAsync(
        IReadOnlyList<EdgeOutboxEntry> batch,
        CancellationToken ct);
}

/// <summary>
/// Default <see cref="IEdgeReplayClient"/> — talks to the configured
/// server via a named <see cref="HttpClient"/>.
/// </summary>
public sealed class EdgeReplayClient : IEdgeReplayClient
{
    /// <summary>Header the server checks against its <c>EdgeNode:SharedSecret</c>.</summary>
    public const string TokenHeader = "X-Edge-Token";

    /// <summary>Logical <see cref="HttpClient"/> name registered in DI.</summary>
    public const string HttpClientName = "edge-replay";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptions<EdgeNodeOptions> _edgeOpts;
    private readonly IOptions<EdgeServerOptions> _serverOpts;
    private readonly ILogger<EdgeReplayClient> _logger;

    public EdgeReplayClient(
        IHttpClientFactory httpFactory,
        IOptions<EdgeNodeOptions> edgeOpts,
        IOptions<EdgeServerOptions> serverOpts,
        ILogger<EdgeReplayClient> logger)
    {
        _httpFactory = httpFactory ?? throw new ArgumentNullException(nameof(httpFactory));
        _edgeOpts = edgeOpts ?? throw new ArgumentNullException(nameof(edgeOpts));
        _serverOpts = serverOpts ?? throw new ArgumentNullException(nameof(serverOpts));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> IsServerReachableAsync(CancellationToken ct)
    {
        var url = _serverOpts.Value.Url;
        if (string.IsNullOrWhiteSpace(url))
        {
            _logger.LogWarning("Server URL is not configured; treating as unreachable.");
            return false;
        }

        try
        {
            using var http = _httpFactory.CreateClient(HttpClientName);
            // Short timeout — we'd rather skip this tick than block the
            // worker loop. The server's /healthz/ready is anonymous +
            // cheap.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var resp = await http.GetAsync(
                new Uri(new Uri(url), "/healthz/ready"),
                cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            _logger.LogDebug("Server probe failed: {Message}", ex.Message);
            return false;
        }
    }

    public async Task<EdgeReplayResponse> SendBatchAsync(
        IReadOnlyList<EdgeOutboxEntry> batch,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
            return new EdgeReplayResponse(Array.Empty<EdgeReplayResult>());

        var serverUrl = _serverOpts.Value.Url;
        if (string.IsNullOrWhiteSpace(serverUrl))
            throw new InvalidOperationException("Server URL is not configured.");

        var edgeNodeId = _edgeOpts.Value.Id;
        if (string.IsNullOrWhiteSpace(edgeNodeId))
            throw new InvalidOperationException("EdgeNode:Id is not configured.");

        using var http = _httpFactory.CreateClient(HttpClientName);
        if (!string.IsNullOrEmpty(_edgeOpts.Value.Token))
            http.DefaultRequestHeaders.TryAddWithoutValidation(TokenHeader, _edgeOpts.Value.Token);

        var body = new EdgeReplayRequest(
            EdgeNodeId: edgeNodeId,
            Events: batch.Select(b => new EdgeReplayEvent(
                EventTypeHint: b.EventTypeHint,
                TenantId: b.TenantId,
                EdgeTimestamp: b.EdgeTimestamp,
                Payload: JsonSerializer.Deserialize<JsonElement>(b.EventPayloadJson)
            )).ToList());

        using var resp = await http.PostAsJsonAsync(
            new Uri(new Uri(serverUrl), "/api/edge/replay"),
            body,
            JsonOptions,
            ct);

        if ((int)resp.StatusCode >= 500)
        {
            // Transient — let the worker mark everything as a 5xx
            // (no permanent error tag).
            throw new HttpRequestException(
                $"Server returned {(int)resp.StatusCode} on edge replay batch (transient).");
        }

        // 2xx and 4xx both deserialize the response body. A 4xx with
        // a parseable response body means per-entry rejection. A 4xx
        // with no body (auth fail, malformed envelope) we surface as
        // a per-entry permanent error so the worker can tag them all.
        if (!resp.IsSuccessStatusCode && resp.Content.Headers.ContentLength is null or 0)
        {
            var msg = $"Server returned {(int)resp.StatusCode} {resp.ReasonPhrase ?? "no body"} on edge replay batch.";
            return new EdgeReplayResponse(
                batch.Select(_ => new EdgeReplayResult(false, msg)).ToList());
        }

        var parsed = await resp.Content.ReadFromJsonAsync<EdgeReplayResponse>(JsonOptions, ct)
            ?? new EdgeReplayResponse(Array.Empty<EdgeReplayResult>());
        return parsed;
    }
}

// ---------------------------------------------------------------------------
// Wire-shape DTOs. Kept under the EdgeNode namespace so both the worker
// and tests can reference the same record types; the server-side
// endpoint deserializes the same shape.
// ---------------------------------------------------------------------------

/// <summary>POST body shape for <c>/api/edge/replay</c>.</summary>
public sealed record EdgeReplayRequest(
    string EdgeNodeId,
    IReadOnlyList<EdgeReplayEvent> Events);

/// <summary>One entry inside the request batch.</summary>
public sealed record EdgeReplayEvent(
    string EventTypeHint,
    long TenantId,
    DateTimeOffset EdgeTimestamp,
    JsonElement Payload);

/// <summary>Response envelope from <c>/api/edge/replay</c>.</summary>
public sealed record EdgeReplayResponse(
    IReadOnlyList<EdgeReplayResult> Results);

/// <summary>Per-entry result. Order matches the request <c>Events</c> list.</summary>
public sealed record EdgeReplayResult(
    bool Ok,
    string? Error);
