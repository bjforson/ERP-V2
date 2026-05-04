using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NickHR.Infrastructure.Data;

/// <summary>
/// Design-time factory for EF Core migrations.
/// </summary>
public class NickHRDbContextFactory : IDesignTimeDbContextFactory<NickHRDbContext>
{
    public NickHRDbContext CreateDbContext(string[] args)
    {
        // Design-time only — runtime startup (Program.cs) handles its own resolution
        // with fail-fast in production. We intentionally do NOT include a Password=
        // fallback here so that running `dotnet ef` without the env var fails loudly
        // instead of silently connecting (or failing opaquely) with a stale password.
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings__DefaultConnection environment variable is not set. " +
                "Set it before running design-time tools (e.g. `dotnet ef migrations add ...`).");
        }

        var optionsBuilder = new DbContextOptionsBuilder<NickHRDbContext>();
        optionsBuilder.UseNpgsql(connectionString);

        return new NickHRDbContext(optionsBuilder.Options);
    }
}
