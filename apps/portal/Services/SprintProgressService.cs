using System.Text.Json;
using System.Text.Json.Serialization;

namespace NickERP.Portal.Services;

/// <summary>
/// Reads the canonical sprint-state JSON at <c>docs/sprint-progress.json</c>
/// and surfaces it to the Portal's Sprint page. The file is the source of
/// truth — humans (and the dispatch agent) update it as work lands; the
/// page just renders.
///
/// File-system access is bounded to the repo's <c>docs/</c> folder under
/// the host's content root. Resolution: <c>{ContentRoot}/../../../../docs/sprint-progress.json</c>
/// for in-repo dev runs, with an env-var override (<c>NICKERP_SPRINT_PROGRESS_PATH</c>)
/// for deployed environments.
/// </summary>
public sealed class SprintProgressService
{
    private readonly string _path;
    private readonly ILogger<SprintProgressService> _logger;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public SprintProgressService(IWebHostEnvironment env, IConfiguration config, ILogger<SprintProgressService> logger)
    {
        _logger = logger;
        var fromEnv = config["NickErp:SprintProgressPath"]
                      ?? Environment.GetEnvironmentVariable("NICKERP_SPRINT_PROGRESS_PATH");
        _path = !string.IsNullOrWhiteSpace(fromEnv)
            ? fromEnv
            // Portal lives at apps/portal; the repo root is two parents up; docs/ from there.
            : Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "..", "docs", "sprint-progress.json"));
        _logger.LogInformation("SprintProgressService reading from {Path}", _path);
    }

    public async Task<SprintProgressDocument?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
        {
            _logger.LogWarning("Sprint progress file not found at {Path}", _path);
            return null;
        }

        try
        {
            await using var fs = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<SprintProgressDocument>(fs, JsonOpts, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse sprint progress JSON at {Path}", _path);
            return null;
        }
    }
}

// ---------------------------------------------------------------------------
// DTOs — match the JSON schema at docs/sprint-progress.json verbatim.
// ---------------------------------------------------------------------------

public sealed class SprintProgressDocument
{
    [JsonPropertyName("currentSprint")] public CurrentSprintInfo? CurrentSprint { get; set; }
    [JsonPropertyName("history")] public List<HistoricalSprint> History { get; set; } = new();
    [JsonPropertyName("backlog")] public List<BacklogItem> Backlog { get; set; } = new();
    [JsonPropertyName("followups")] public List<Followup> Followups { get; set; } = new();
}

public sealed class CurrentSprintInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("goal")] public string Goal { get; set; } = "";
    [JsonPropertyName("startedAt")] public string StartedAt { get; set; } = "";
    [JsonPropertyName("items")] public List<WorkItem> Items { get; set; } = new();
}

public sealed class WorkItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("phase")] public string Phase { get; set; } = "";
    /// <summary><c>pending</c> | <c>in-progress</c> | <c>review</c> | <c>done</c> | <c>deferred</c>.</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "pending";
    [JsonPropertyName("branch")] public string? Branch { get; set; }
    [JsonPropertyName("commit")] public string? Commit { get; set; }
    [JsonPropertyName("agentId")] public string? AgentId { get; set; }
    [JsonPropertyName("dispatchedAt")] public string? DispatchedAt { get; set; }
    [JsonPropertyName("completedAt")] public string? CompletedAt { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
}

public sealed class HistoricalSprint
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("shippedAt")] public string ShippedAt { get; set; } = "";
    [JsonPropertyName("mainCommit")] public string MainCommit { get; set; } = "";
    [JsonPropertyName("itemCount")] public int ItemCount { get; set; }
    [JsonPropertyName("items")] public List<HistoricalItem> Items { get; set; } = new();
}

public sealed class HistoricalItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("commit")] public string Commit { get; set; } = "";
}

public sealed class BacklogItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("phase")] public string Phase { get; set; } = "";
    [JsonPropertyName("estimateDays")] public string EstimateDays { get; set; } = "";
}

public sealed class Followup
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("from")] public string From { get; set; } = "";
    [JsonPropertyName("severity")] public string Severity { get; set; } = "";
}
