using Microsoft.EntityFrameworkCore;
using NickERP.NickFinance.Core.Entities;

namespace NickERP.NickFinance.Database;

/// <summary>
/// EF Core DbContext for the NickFinance v2 module — see G2 §1.6, §1.7,
/// §1.10. Lives in its OWN PostgreSQL database
/// (<c>nickerp_nickfinance</c>) — modules own their data per the
/// platform contract. Default schema <c>nickfinance</c>.
///
/// <para>
/// Tables in this context:
/// </para>
/// <list type="bullet">
///   <item><description><c>petty_cash_boxes</c> — tenant-isolated, RLS-enforced.</description></item>
///   <item><description><c>petty_cash_vouchers</c> — tenant-isolated.</description></item>
///   <item><description><c>petty_cash_ledger_events</c> — tenant-isolated, append-only.</description></item>
///   <item><description><c>petty_cash_periods</c> — tenant-isolated, composite (TenantId, PeriodYearMonth) PK.</description></item>
///   <item><description><c>fx_rate</c> — suite-wide, NOT under standard tenant RLS; opt-in to <c>app.tenant_id = '-1'</c> for NULL-tenant rows.</description></item>
/// </list>
/// </summary>
public sealed class NickFinanceDbContext : DbContext
{
    /// <summary>Default schema name for every NickFinance table.</summary>
    public const string SchemaName = "nickfinance";

    public NickFinanceDbContext(DbContextOptions<NickFinanceDbContext> options) : base(options) { }

    public DbSet<PettyCashBox> Boxes => Set<PettyCashBox>();
    public DbSet<Voucher> Vouchers => Set<Voucher>();
    public DbSet<LedgerEvent> LedgerEvents => Set<LedgerEvent>();
    public DbSet<PettyCashPeriod> Periods => Set<PettyCashPeriod>();
    public DbSet<FxRate> FxRates => Set<FxRate>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        // ----- PettyCashBox ----------------------------------------------------
        modelBuilder.Entity<PettyCashBox>(e =>
        {
            e.ToTable("petty_cash_boxes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Code).IsRequired().HasMaxLength(64);
            e.Property(x => x.Name).IsRequired().HasMaxLength(200);
            e.Property(x => x.CurrencyCode).IsRequired().HasMaxLength(3);
            e.Property(x => x.CustodianUserId).IsRequired();
            e.Property(x => x.ApproverUserId).IsRequired();
            e.Property(x => x.OpeningBalanceAmount).HasColumnType("numeric(18,4)").HasDefaultValue(0m);
            e.Property(x => x.OpeningBalanceCurrency).IsRequired().HasMaxLength(3);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.TenantId).IsRequired();

            e.HasIndex(x => new { x.TenantId, x.Code })
                .IsUnique()
                .HasDatabaseName("ux_petty_cash_boxes_tenant_code");
            e.HasIndex(x => x.TenantId).HasDatabaseName("ix_petty_cash_boxes_tenant");

            // Separation-of-duties: custodian and approver MUST be different.
            e.ToTable(t => t.HasCheckConstraint(
                "ck_petty_cash_boxes_custodian_neq_approver",
                "\"CustodianUserId\" <> \"ApproverUserId\""));
            // Opening balance currency must match box currency.
            e.ToTable(t => t.HasCheckConstraint(
                "ck_petty_cash_boxes_opening_currency_match",
                "\"OpeningBalanceCurrency\" = \"CurrencyCode\""));
        });

        // ----- Voucher ---------------------------------------------------------
        modelBuilder.Entity<Voucher>(e =>
        {
            e.ToTable("petty_cash_vouchers");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.BoxId).IsRequired();
            e.Property(x => x.SequenceNumber).IsRequired();
            // State stored as int via EF default value-converter on the enum;
            // text is more forensic-friendly but int is simpler and the migration
            // generates the correct shape from EF's metadata.
            e.Property(x => x.State).HasConversion<int>().IsRequired();
            e.Property(x => x.Purpose).IsRequired().HasMaxLength(2000);
            e.Property(x => x.RequestedAmount).HasColumnType("numeric(18,4)").IsRequired();
            e.Property(x => x.RequestedCurrency).IsRequired().HasMaxLength(3);
            e.Property(x => x.RequestedAmountBase).HasColumnType("numeric(18,4)").IsRequired();
            e.Property(x => x.RequestedCurrencyBase).IsRequired().HasMaxLength(3);
            e.Property(x => x.RequestedByUserId).IsRequired();
            e.Property(x => x.RequestedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.ApproverUserId);
            e.Property(x => x.ApprovedAt);
            e.Property(x => x.DisbursedAmount).HasColumnType("numeric(18,4)");
            e.Property(x => x.DisbursedCurrency).HasMaxLength(3);
            e.Property(x => x.DisbursedAmountBase).HasColumnType("numeric(18,4)");
            e.Property(x => x.DisbursedCurrencyBase).HasMaxLength(3);
            e.Property(x => x.DisbursedAt);
            e.Property(x => x.ReconciledAt);
            e.Property(x => x.RejectedReason).HasMaxLength(2000);
            e.Property(x => x.CancelledAt);
            e.Property(x => x.TenantId).IsRequired();

            e.HasOne(x => x.Box)
                .WithMany()
                .HasForeignKey(x => x.BoxId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.TenantId, x.BoxId, x.SequenceNumber })
                .IsUnique()
                .HasDatabaseName("ux_petty_cash_vouchers_tenant_box_seq");
            e.HasIndex(x => new { x.TenantId, x.BoxId, x.State })
                .HasDatabaseName("ix_petty_cash_vouchers_tenant_box_state");
            e.HasIndex(x => x.RequestedByUserId)
                .HasDatabaseName("ix_petty_cash_vouchers_requester");
        });

        // ----- LedgerEvent (append-only) ---------------------------------------
        modelBuilder.Entity<LedgerEvent>(e =>
        {
            e.ToTable("petty_cash_ledger_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.BoxId).IsRequired();
            e.Property(x => x.VoucherId);
            e.Property(x => x.EventType).HasConversion<int>().IsRequired();
            e.Property(x => x.Direction).HasConversion<int>().IsRequired();
            e.Property(x => x.AmountNative).HasColumnType("numeric(18,4)").IsRequired();
            e.Property(x => x.CurrencyNative).IsRequired().HasMaxLength(3);
            e.Property(x => x.AmountBase).HasColumnType("numeric(18,4)").IsRequired();
            e.Property(x => x.CurrencyBase).IsRequired().HasMaxLength(3);
            e.Property(x => x.FxRate).HasColumnType("numeric(18,8)").IsRequired();
            e.Property(x => x.FxRateDate).HasColumnType("date").IsRequired();
            e.Property(x => x.PostedAt).IsRequired();
            e.Property(x => x.PostedByUserId).IsRequired();
            e.Property(x => x.CorrectsEventId);
            e.Property(x => x.TenantId).IsRequired();

            e.HasIndex(x => new { x.TenantId, x.BoxId, x.PostedAt })
                .HasDatabaseName("ix_ledger_tenant_box_posted");
            e.HasIndex(x => x.VoucherId)
                .HasDatabaseName("ix_ledger_voucher");
            e.HasIndex(x => x.CorrectsEventId)
                .HasDatabaseName("ix_ledger_corrects");

            // Money invariant at the DB layer too — defence in depth.
            e.ToTable(t => t.HasCheckConstraint(
                "ck_ledger_amount_native_nonneg",
                "\"AmountNative\" >= 0"));
            e.ToTable(t => t.HasCheckConstraint(
                "ck_ledger_amount_base_nonneg",
                "\"AmountBase\" >= 0"));
        });

        // ----- PettyCashPeriod -------------------------------------------------
        modelBuilder.Entity<PettyCashPeriod>(e =>
        {
            e.ToTable("petty_cash_periods");
            e.HasKey(x => new { x.TenantId, x.PeriodYearMonth });
            e.Property(x => x.PeriodYearMonth).IsRequired().HasMaxLength(7);
            e.Property(x => x.ClosedAt);
            e.Property(x => x.ClosedByUserId);
        });

        // ----- FxRate (suite-wide) ---------------------------------------------
        modelBuilder.Entity<FxRate>(e =>
        {
            e.ToTable("fx_rate");
            e.HasKey(x => new { x.FromCurrency, x.ToCurrency, x.EffectiveDate });
            e.Property(x => x.TenantId);
            e.Property(x => x.FromCurrency).IsRequired().HasMaxLength(3);
            e.Property(x => x.ToCurrency).IsRequired().HasMaxLength(3);
            e.Property(x => x.Rate).HasColumnType("numeric(18,8)").IsRequired();
            e.Property(x => x.EffectiveDate).HasColumnType("date").IsRequired();
            e.Property(x => x.PublishedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.PublishedByUserId).IsRequired();

            e.HasIndex(x => new { x.FromCurrency, x.ToCurrency, x.EffectiveDate })
                .IsDescending(false, false, true)
                .HasDatabaseName("ix_fx_rate_pair_effective_desc");

            // Rate must be positive — a zero or negative rate would silently
            // zero out base amounts in the ledger.
            e.ToTable(t => t.HasCheckConstraint(
                "ck_fx_rate_positive",
                "\"Rate\" > 0"));
        });
    }
}

/// <summary>
/// Design-time factory for <c>dotnet ef migrations</c>. Reads
/// <c>NICKERP_NICKFINANCE_DB_CONNECTION</c> with a localhost fallback —
/// mirrors the inspection / audit / identity factories.
/// </summary>
public sealed class NickFinanceDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<NickFinanceDbContext>
{
    public NickFinanceDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("NICKERP_NICKFINANCE_DB_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=nickerp_nickfinance;Username=postgres;Password=designtime";

        var options = new DbContextOptionsBuilder<NickFinanceDbContext>()
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(NickFinanceDbContext).Assembly.GetName().Name);
                // Mirror inspection's H3 — keep EF's history table inside the
                // module schema so nscim_app never needs CREATE on `public`.
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "nickfinance");
            })
            .Options;

        return new NickFinanceDbContext(options);
    }
}
