using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace NickERP.Platform.Logging;

/// <summary>
/// Stamps every log event with a <c>CorrelationId</c> property so a single
/// request can be traced across services in Seq.
/// <para>
/// Resolution order:
/// <list type="number">
///   <item><description><c>Activity.Current.RootId</c> — set by ASP.NET Core / OpenTelemetry per inbound request.</description></item>
///   <item><description><c>Activity.Current.Id</c> — fallback for non-root spans.</description></item>
///   <item><description>Newly generated GUID — for background work outside any request.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class CorrelationIdEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        if (logEvent.Properties.ContainsKey("CorrelationId"))
        {
            return;
        }

        var correlationId =
            Activity.Current?.RootId
            ?? Activity.Current?.Id
            ?? Guid.NewGuid().ToString("N");

        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("CorrelationId", correlationId));
    }
}
