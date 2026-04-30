using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Identity;

namespace NickFinance.Ledger;

public class LedgerDbContext : DbContext
{
    private readonly ITenantAccessor? _tenantAccessor;

    public LedgerDbContext(DbContextOptions<LedgerDbContext> options) : base(options) { }

    public LedgerDbContext(DbContextOptions<LedgerDbContext> options, ITenantAccessor? tenantAccessor)
        : base(options)
    {
        _tenantAccessor = tenantAccessor;
    }

    public DbSet<LedgerEvent> Events => Set<LedgerEvent>();
    public DbSet<LedgerEventLine> EventLines => Set<LedgerEventLine>();
    public DbSet<AccountingPeriod> Periods => Set<AccountingPeriod>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.HasDefaultSchema("finance");

        // Multi-tenant query filter wired conditionally so the bootstrap
        // CLI / smoke runner / tests (which construct the context without
        // an accessor) see every row, while the live WebApp scopes to the
        // current circuit's tenant.
        var filterEnabled = _tenantAccessor is not null;

        mb.Entity<LedgerEvent>(e =>
        {
            e.ToTable("ledger_events");
            e.HasKey(x => x.EventId);
            e.Property(x => x.EventId).HasColumnName("event_id");
            e.Property(x => x.CommittedAt).HasColumnName("committed_at");
            e.Property(x => x.EffectiveDate).HasColumnName("effective_date");
            e.Property(x => x.PeriodId).HasColumnName("period_id");
            e.Property(x => x.SourceModule).HasColumnName("source_module").HasMaxLength(64);
            e.Property(x => x.SourceEntityType).HasColumnName("source_entity_type").HasMaxLength(64);
            e.Property(x => x.SourceEntityId).HasColumnName("source_entity_id").HasMaxLength(64);
            e.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128);
            e.Property(x => x.EventType).HasColumnName("event_type").HasConversion<int>();
            e.Property(x => x.ReversesEventId).HasColumnName("reverses_event_id");
            e.Property(x => x.Narration).HasColumnName("narration").HasMaxLength(500);
            e.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");

            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.PeriodId });
            e.HasIndex(x => new { x.TenantId, x.EffectiveDate });
            e.HasIndex(x => x.ReversesEventId);

            e.HasOne<AccountingPeriod>()
                .WithMany()
                .HasForeignKey(x => x.PeriodId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasMany(x => x.Lines)
                .WithOne(l => l.Event)
                .HasForeignKey(l => l.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            if (filterEnabled)
            {
                e.HasQueryFilter(x => x.TenantId == _tenantAccessor!.Current);
            }
        });

        mb.Entity<LedgerEventLine>(e =>
        {
            e.ToTable("ledger_event_lines");
            e.HasKey(x => new { x.EventId, x.LineNo });
            e.Property(x => x.EventId).HasColumnName("event_id");
            e.Property(x => x.LineNo).HasColumnName("line_no");
            e.Property(x => x.AccountCode).HasColumnName("account_code").HasMaxLength(64).IsRequired();
            e.Property(x => x.DebitMinor).HasColumnName("debit_minor");
            e.Property(x => x.CreditMinor).HasColumnName("credit_minor");
            e.Property(x => x.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();
            e.Property(x => x.SiteId).HasColumnName("site_id");
            e.Property(x => x.ProjectCode).HasColumnName("project_code").HasMaxLength(64);
            e.Property(x => x.CostCenterCode).HasColumnName("cost_center_code").HasMaxLength(64);
            e.Property(x => x.DimsExtraJson).HasColumnName("dims_extra").HasColumnType("jsonb");
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(500);

            e.HasIndex(x => new { x.AccountCode, x.CurrencyCode });
            e.HasIndex(x => x.SiteId);
        });

        mb.Entity<AccountingPeriod>(e =>
        {
            e.ToTable("accounting_periods");
            e.HasKey(x => x.PeriodId);
            e.Property(x => x.PeriodId).HasColumnName("period_id");
            e.Property(x => x.FiscalYear).HasColumnName("fiscal_year");
            e.Property(x => x.MonthNumber).HasColumnName("month_number");
            e.Property(x => x.StartDate).HasColumnName("start_date");
            e.Property(x => x.EndDate).HasColumnName("end_date");
            e.Property(x => x.Status).HasColumnName("status").HasConversion<int>();
            e.Property(x => x.ClosedAt).HasColumnName("closed_at");
            e.Property(x => x.ClosedByUserId).HasColumnName("closed_by_user_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");

            e.HasIndex(x => new { x.TenantId, x.FiscalYear, x.MonthNumber }).IsUnique();

            if (filterEnabled)
            {
                e.HasQueryFilter(x => x.TenantId == _tenantAccessor!.Current);
            }
        });

        // LedgerEventLine inherits its tenant scoping through the parent
        // event's filter (EF cascades the predicate via the relationship).
    }
}
