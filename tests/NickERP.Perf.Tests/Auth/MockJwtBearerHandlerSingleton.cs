namespace NickERP.Perf.Tests.Auth;

/// <summary>
/// Sprint 55 — process-wide singleton holder for
/// <see cref="MockJwtBearerHandler"/>. Both the case-create scenario and
/// the edge-replay scenario want one handler per run so the API host's
/// JWKS-mock can validate every scenario's tokens against the same kid.
/// </summary>
/// <remarks>
/// <para>
/// The handler holds an RSA-2048 key pair created at first access; the
/// <c>kid</c> stays stable for the process lifetime. Disposal is via
/// process exit — there's no <c>Dispose</c> ladder because the perf
/// runner owns the run lifecycle and the harness exits when the run
/// completes.
/// </para>
/// </remarks>
public static class MockJwtBearerHandlerSingleton
{
    private static MockJwtBearerHandler? _instance;
    private static readonly object _lock = new();

    /// <summary>The process-wide handler. Lazily created on first read.</summary>
    public static MockJwtBearerHandler Instance
    {
        get
        {
            if (_instance is not null) return _instance;
            lock (_lock)
            {
                _instance ??= new MockJwtBearerHandler();
            }
            return _instance;
        }
    }

    /// <summary>
    /// Reset the singleton. Test-only — never call from production paths.
    /// Used by unit tests to verify the lazy-init behaviour.
    /// </summary>
    public static void Reset()
    {
        lock (_lock)
        {
            _instance?.Dispose();
            _instance = null;
        }
    }
}
