using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace NickFinance.PettyCash.Approvals;

/// <summary>
/// Parses a YAML file conforming to <c>PETTY_CASH.md §5.1</c> into an
/// in-memory <see cref="ApprovalPolicy"/>. Hot-reload is a v1.2 concern —
/// callers reload by re-instantiating <see cref="PolicyApprovalEngine"/>
/// with a freshly-loaded policy.
/// </summary>
/// <remarks>
/// <para>
/// Recognised top-level shape:
/// </para>
/// <code>
/// version: "2026-04-26"
/// escalation:
///   default_hours: 48
///   default_target: site_supervisor
/// categories:
///   TRANSPORT:
///     bands:
///       - { max: 20000,  steps: [line_manager] }
///       - { max: 100000, steps: [line_manager, [site_supervisor, finance]] }   # parallel
///       - { max: 500000, steps: [[line_manager, site_supervisor], finance] }   # mixed
/// </code>
/// <para>
/// Each step is either a scalar role string (a single approver) or a
/// nested sequence of role strings (parallel approvers — all must
/// approve before the chain advances).
/// </para>
/// <para>
/// Optional per-band escalation override:
/// </para>
/// <code>
/// bands:
///   - max: 1000000
///     steps:
///       - { roles: [finance, cfo], escalate_after_hours: 8, escalate_to: chairman }
/// </code>
/// </remarks>
public static class ApprovalPolicyYamlLoader
{
    private static readonly IDeserializer _yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static ApprovalPolicy Load(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            throw new ArgumentException("YAML content is empty.", nameof(yaml));
        }

        Root? root;
        try { root = _yaml.Deserialize<Root>(yaml); }
        catch (Exception ex) { throw new PettyCashException($"Failed to parse approval policy YAML: {ex.Message}", ex); }
        if (root is null) throw new PettyCashException("Approval policy YAML produced an empty document.");

        var version = string.IsNullOrWhiteSpace(root.Version)
            ? throw new PettyCashException("`version` is required at the top level of the approval policy.")
            : root.Version!.Trim();
        if (root.Categories is null || root.Categories.Count == 0)
        {
            throw new PettyCashException("`categories` is required and must contain at least one entry.");
        }

        var defaultHours = root.Escalation?.DefaultHours;
        var defaultTarget = root.Escalation?.DefaultTarget;

        var built = new Dictionary<VoucherCategory, IReadOnlyList<ApprovalBand>>();
        foreach (var (rawName, catBody) in root.Categories)
        {
            if (!Enum.TryParse<VoucherCategory>(rawName, ignoreCase: true, out var cat))
            {
                throw new PettyCashException(
                    $"Unknown category '{rawName}' in approval policy. Known: {string.Join(", ", Enum.GetNames<VoucherCategory>())}.");
            }
            if (catBody?.Bands is null || catBody.Bands.Count == 0)
            {
                throw new PettyCashException($"Category '{rawName}' has no bands.");
            }

            var bands = new List<ApprovalBand>(catBody.Bands.Count);
            long? lastMax = null;
            foreach (var b in catBody.Bands)
            {
                if (b.Max <= 0)
                {
                    throw new PettyCashException($"Band in category '{rawName}' has non-positive max ({b.Max}).");
                }
                if (b.Steps is null || b.Steps.Count == 0)
                {
                    throw new PettyCashException($"Band in category '{rawName}' (max {b.Max}) has no steps.");
                }
                if (lastMax is not null && b.Max <= lastMax.Value)
                {
                    throw new PettyCashException(
                        $"Bands in category '{rawName}' must be in ascending `max` order; got {lastMax} -> {b.Max}.");
                }

                var steps = new List<ApprovalStep>(b.Steps.Count);
                foreach (var rawStep in b.Steps)
                {
                    steps.Add(NormaliseStep(rawStep, defaultHours, defaultTarget, rawName));
                }
                bands.Add(new ApprovalBand(b.Max, steps));
                lastMax = b.Max;
            }
            built[cat] = bands;
        }

        return new ApprovalPolicy(version, built);
    }

    public static ApprovalPolicy LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Load(File.ReadAllText(path));
    }

    // ---------------------------------------------------------------------
    // Step normalisation. A "step" entry can be:
    //   1. a scalar role:  "line_manager"
    //   2. a sequence of roles (parallel): ["site_supervisor", "finance"]
    //   3. a mapping with explicit options: { roles: [...], escalate_after_hours: N, escalate_to: X }
    // ---------------------------------------------------------------------

    private static ApprovalStep NormaliseStep(object? raw, int? defaultHours, string? defaultTarget, string categoryName)
    {
        switch (raw)
        {
            case null:
                throw new PettyCashException($"Step entry in category '{categoryName}' is null.");

            case string scalar:
                return new ApprovalStep(
                    Roles: new[] { scalar.Trim() },
                    EscalateAfterHours: defaultHours,
                    EscalateTo: defaultTarget);

            case IEnumerable<object?> seq:
                {
                    var roles = seq.Select(r =>
                        r as string ?? throw new PettyCashException(
                            $"Inline parallel step in category '{categoryName}' must be a sequence of role names."))
                        .Select(s => s.Trim())
                        .ToList();
                    if (roles.Count == 0)
                    {
                        throw new PettyCashException($"Empty parallel step in category '{categoryName}'.");
                    }
                    return new ApprovalStep(roles, defaultHours, defaultTarget);
                }

            case IDictionary<object, object?> map:
                {
                    if (!map.TryGetValue("roles", out var rolesObj) || rolesObj is not IEnumerable<object?> rolesSeq)
                    {
                        throw new PettyCashException($"Step mapping in category '{categoryName}' must have a `roles` sequence.");
                    }
                    var roles = rolesSeq.Select(r =>
                        r as string ?? throw new PettyCashException(
                            $"Step `roles` in category '{categoryName}' must be strings."))
                        .Select(s => s.Trim())
                        .ToList();
                    int? hours = defaultHours;
                    string? target = defaultTarget;
                    if (map.TryGetValue("escalate_after_hours", out var hVal) && hVal is not null)
                    {
                        if (!int.TryParse(hVal.ToString(), out var h) || h <= 0)
                        {
                            throw new PettyCashException($"Step `escalate_after_hours` in category '{categoryName}' must be a positive int.");
                        }
                        hours = h;
                    }
                    if (map.TryGetValue("escalate_to", out var toVal) && toVal is string toStr && !string.IsNullOrWhiteSpace(toStr))
                    {
                        target = toStr.Trim();
                    }
                    return new ApprovalStep(roles, hours, target);
                }

            default:
                throw new PettyCashException(
                    $"Step in category '{categoryName}' has unsupported YAML shape: {raw.GetType().Name}.");
        }
    }

    // ---------------------------------------------------------------------
    // YAML shape DTOs
    // ---------------------------------------------------------------------

    private sealed class Root
    {
        public string? Version { get; set; }
        public EscalationBody? Escalation { get; set; }
        public Dictionary<string, CategoryBody>? Categories { get; set; }
    }

    private sealed class EscalationBody
    {
        public int? DefaultHours { get; set; }
        public string? DefaultTarget { get; set; }
    }

    private sealed class CategoryBody
    {
        public List<BandBody>? Bands { get; set; }
    }

    private sealed class BandBody
    {
        public long Max { get; set; }
        public List<object?>? Steps { get; set; }
    }
}
