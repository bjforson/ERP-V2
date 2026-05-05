using NickERP.Tools.PerfSeed;

// ----------------------------------------------------------------------------
// Sprint 52 / FU-perf-tenant-data-shape — perf-seed CLI entry point.
//
// Usage:
//   dotnet run --project tools/perf-seed -- --scale 1000 --tenants 3
//
// Defaults: scale=1000 cases per tenant, tenants=3.
//
// Required env vars (same shape as the platform DbContext factories):
//   NICKERP_PLATFORM_DB_CONNECTION    — postgres superuser to nickerp_platform
//   NICKERP_INSPECTION_DB_CONNECTION  — postgres superuser to nickerp_inspection
//
// Why the postgres superuser: this tool seeds business data across both
// DBs in bulk. nscim_app is the production app role and only has the
// minimum CRUD set; the seed needs to insert into tenancy.tenants which
// is admin-flavored. Operator MUST NOT use this tool against a
// production DB — it tags every row IsSynthetic=true so the
// gate.analyst.decisioned_real_case probe ignores them, but mixing
// synthetic + production data still violates the perf-test isolation
// principle in test-plan.md §1.
// ----------------------------------------------------------------------------

var (scale, tenantCount) = ParseArgs(args);

Console.WriteLine($"NickERP perf-seed — scale={scale} per tenant, tenants={tenantCount}");

var platformConn = Environment.GetEnvironmentVariable("NICKERP_PLATFORM_DB_CONNECTION");
var inspectionConn = Environment.GetEnvironmentVariable("NICKERP_INSPECTION_DB_CONNECTION");
if (string.IsNullOrWhiteSpace(platformConn) || string.IsNullOrWhiteSpace(inspectionConn))
{
    Console.Error.WriteLine(
        "ERROR: NICKERP_PLATFORM_DB_CONNECTION + NICKERP_INSPECTION_DB_CONNECTION must be set.");
    return 2;
}

var seeder = new PerfSeeder(platformConn, inspectionConn);
var summary = await seeder.SeedAsync(scale, tenantCount);

Console.WriteLine($"Seeded: {summary}");
return 0;

static (int scale, int tenants) ParseArgs(string[] argv)
{
    int scale = 1000;
    int tenants = 3;
    for (var i = 0; i < argv.Length; i++)
    {
        switch (argv[i])
        {
            case "--scale" when i + 1 < argv.Length:
                scale = int.Parse(argv[++i]);
                break;
            case "--tenants" when i + 1 < argv.Length:
                tenants = int.Parse(argv[++i]);
                break;
        }
    }
    return (scale, tenants);
}
