using System.Diagnostics.Metrics;
using NickERP.Platform.Logging;
using NickERP.Platform.Telemetry;

const string ServiceName = "Platform.Demos.Observability";

var builder = WebApplication.CreateBuilder(args);

// Track A.1.2 — structured logs to Seq + per-service file + console
builder.UseNickErpLogging(ServiceName);

// Track A.1.3 — OTel tracing + metrics to Seq's OTLP receiver
builder.UseNickErpTelemetry(ServiceName);

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Demo metric — counter incremented per request
var requestsServed = NickErpActivity.Meter.CreateCounter<long>(
    "nickerp.demos.observability.requests_served",
    description: "Total demo requests served, by endpoint.");

// GET /demo — exercises log + custom span + counter
app.MapGet("/demo", (ILogger<Program> log, string? caseId) =>
{
    using var span = NickErpActivity.Source.StartActivity("demo.handle_request");
    span?.SetTag("demo.case_id", caseId ?? "(none)");

    log.LogInformation("Demo request received for case {CaseId}", caseId ?? "(none)");

    // Synthetic work the trace can show
    using (var inner = NickErpActivity.Source.StartActivity("demo.synthetic_work"))
    {
        inner?.SetTag("work.kind", "delay");
        Thread.Sleep(25);
    }

    requestsServed.Add(1, new KeyValuePair<string, object?>("endpoint", "demo"));

    return Results.Ok(new
    {
        service = ServiceName,
        traceId = span?.TraceId.ToString(),
        spanId = span?.SpanId.ToString(),
        caseId,
        message = "log + trace + metric all emitted; check Seq at http://localhost:5341"
    });
})
.WithName("Demo");

// GET /boom — exercises error logging + span exception recording
app.MapGet("/boom", (ILogger<Program> log) =>
{
    using var span = NickErpActivity.Source.StartActivity("demo.boom");
    try
    {
        throw new InvalidOperationException("synthetic failure for observability demo");
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Demo /boom endpoint hit; raising synthetic exception");
        span?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
        span?.AddException(ex);
        return Results.Problem(detail: ex.Message, statusCode: 500);
    }
})
.WithName("Boom");

app.Run();
