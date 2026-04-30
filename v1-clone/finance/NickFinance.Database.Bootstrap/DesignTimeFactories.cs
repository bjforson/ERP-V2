using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NickFinance.AP;
using NickFinance.AR;
using NickFinance.Banking;
using NickFinance.Budgeting;
using NickFinance.FixedAssets;
using NickFinance.Coa;
using NickERP.Platform.Identity;
using NickFinance.Ledger;
using NickFinance.PettyCash;

namespace NickFinance.Database.Bootstrap;

/// <summary>
/// Design-time DbContext factory used by <c>dotnet ef migrations add</c>
/// and other tooling that needs to instantiate the context outside of a
/// running app. Returns a context wired to a placeholder connection string
/// — migration generation only inspects the model, never opens a real
/// connection.
/// </summary>
public sealed class LedgerDesignTimeFactory : IDesignTimeDbContextFactory<LedgerDbContext>
{
    public LedgerDbContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("NICKERP_FINANCE_DB_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=nickerp_design_only;Username=postgres;Password=design";
        var opts = new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(conn).Options;
        return new LedgerDbContext(opts);
    }
}

/// <inheritdoc cref="LedgerDesignTimeFactory"/>
public sealed class PettyCashDesignTimeFactory : IDesignTimeDbContextFactory<PettyCashDbContext>
{
    public PettyCashDbContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("NICKERP_FINANCE_DB_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=nickerp_design_only;Username=postgres;Password=design";
        var opts = new DbContextOptionsBuilder<PettyCashDbContext>().UseNpgsql(conn).Options;
        return new PettyCashDbContext(opts);
    }
}

/// <inheritdoc cref="LedgerDesignTimeFactory"/>
public sealed class CoaDesignTimeFactory : IDesignTimeDbContextFactory<CoaDbContext>
{
    public CoaDbContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("NICKERP_FINANCE_DB_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=nickerp_design_only;Username=postgres;Password=design";
        var opts = new DbContextOptionsBuilder<CoaDbContext>().UseNpgsql(conn).Options;
        return new CoaDbContext(opts);
    }
}

/// <inheritdoc cref="LedgerDesignTimeFactory"/>
public sealed class ArDesignTimeFactory : IDesignTimeDbContextFactory<ArDbContext>
{
    public ArDbContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("NICKERP_FINANCE_DB_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=nickerp_design_only;Username=postgres;Password=design";
        var opts = new DbContextOptionsBuilder<ArDbContext>().UseNpgsql(conn).Options;
        return new ArDbContext(opts);
    }
}

/// <inheritdoc cref="LedgerDesignTimeFactory"/>
public sealed class ApDesignTimeFactory : IDesignTimeDbContextFactory<ApDbContext>
{
    public ApDbContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("NICKERP_FINANCE_DB_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=nickerp_design_only;Username=postgres;Password=design";
        var opts = new DbContextOptionsBuilder<ApDbContext>().UseNpgsql(conn).Options;
        return new ApDbContext(opts);
    }
}

/// <inheritdoc cref="LedgerDesignTimeFactory"/>
public sealed class BankingDesignTimeFactory : IDesignTimeDbContextFactory<BankingDbContext>
{
    public BankingDbContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("NICKERP_FINANCE_DB_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=nickerp_design_only;Username=postgres;Password=design";
        var opts = new DbContextOptionsBuilder<BankingDbContext>().UseNpgsql(conn).Options;
        return new BankingDbContext(opts);
    }
}

/// <inheritdoc cref="LedgerDesignTimeFactory"/>
public sealed class FixedAssetsDesignTimeFactory : IDesignTimeDbContextFactory<FixedAssetsDbContext>
{
    public FixedAssetsDbContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("NICKERP_FINANCE_DB_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=nickerp_design_only;Username=postgres;Password=design";
        var opts = new DbContextOptionsBuilder<FixedAssetsDbContext>().UseNpgsql(conn).Options;
        return new FixedAssetsDbContext(opts);
    }
}

/// <inheritdoc cref="LedgerDesignTimeFactory"/>
public sealed class BudgetingDesignTimeFactory : IDesignTimeDbContextFactory<BudgetingDbContext>
{
    public BudgetingDbContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("NICKERP_FINANCE_DB_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=nickerp_design_only;Username=postgres;Password=design";
        var opts = new DbContextOptionsBuilder<BudgetingDbContext>().UseNpgsql(conn).Options;
        return new BudgetingDbContext(opts);
    }
}

/// <inheritdoc cref="LedgerDesignTimeFactory"/>
public sealed class IdentityDesignTimeFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("NICKERP_FINANCE_DB_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=nickerp_design_only;Username=postgres;Password=design";
        var opts = new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(conn).Options;
        // No tenant accessor at design time — query filters disabled; the
        // generated migration shape is the same either way.
        return new IdentityDbContext(opts);
    }
}
