using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Phase F5 — keeps raw <see cref="Exception.Message"/> out of the UI.
///
/// <para>
/// The Razor pages used to do <c>catch (Exception ex) { _message = ex.Message; }</c>,
/// which leaks SQL state, stack hints, and arbitrary internal text to whoever
/// is logged in. <see cref="Render"/> replaces that pattern: the full
/// exception is logged with a correlation id; the UI only shows the
/// safe message + the correlation id (so support can grep the logs).
/// </para>
///
/// <para>
/// Correlation id is taken from <see cref="Activity.Current"/> when a
/// trace context exists (the OTEL hook in Program.cs starts one per
/// request); otherwise a fresh GUID stem is used. Either way the full
/// log entry includes both <c>code</c> and <c>correlationId</c> as
/// structured properties so the alert pipeline can pick them up.
/// </para>
/// </summary>
public static class UserFacingError
{
    /// <summary>
    /// Log the full exception under <paramref name="code"/> and return
    /// a UI-safe message of the form <c>"{safeMessage} ({correlationId})"</c>.
    /// </summary>
    /// <param name="logger">Page or service logger.</param>
    /// <param name="ex">The exception caught. Logged at <c>Error</c> level with full stack.</param>
    /// <param name="code">Stable error code, e.g. <c>"INSP-CASE-OPEN-FAILED"</c>. Used as the log message template.</param>
    /// <param name="safeMessage">User-facing summary. Plain English, no implementation detail.</param>
    /// <returns>The string to assign to a Razor page's <c>_message</c> field.</returns>
    public static string Render(ILogger logger, Exception ex, string code, string safeMessage)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(ex);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(safeMessage);

        var correlationId = ResolveCorrelationId();

        // Logging scope makes the correlation id show up on every log line
        // emitted while inside the using block — handy when a downstream
        // logger nested under the catch also fires.
        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["UserFacingErrorCode"] = code,
            ["CorrelationId"] = correlationId
        }))
        {
            logger.LogError(ex,
                "User-facing error {Code} (correlation {CorrelationId}): {SafeMessage}",
                code, correlationId, safeMessage);
        }

        return $"{safeMessage} ({correlationId})";
    }

    private static string ResolveCorrelationId()
    {
        var activity = Activity.Current;
        if (activity is not null && !string.IsNullOrEmpty(activity.TraceId.ToString()))
        {
            // First 16 hex chars of the trace id — short enough for an
            // analyst to read aloud over the phone, long enough to be
            // unique across a working day.
            return activity.TraceId.ToString()[..16];
        }
        return Guid.NewGuid().ToString("N")[..16];
    }
}
