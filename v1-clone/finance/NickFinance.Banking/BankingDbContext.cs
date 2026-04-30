using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Identity;

namespace NickFinance.Banking;

public class BankingDbContext : DbContext
{
    public const string SchemaName = "banking";

    private readonly ITenantAccessor? _tenantAccessor;

    public BankingDbContext(DbContextOptions<BankingDbContext> options) : base(options) { }

    public BankingDbContext(DbContextOptions<BankingDbContext> options, ITenantAccessor? tenantAccessor)
        : base(options)
    {
        _tenantAccessor = tenantAccessor;
    }

    public DbSet<BankAccount> BankAccounts => Set<BankAccount>();
    public DbSet<BankStatement> Statements => Set<BankStatement>();
    public DbSet<BankTransaction> Transactions => Set<BankTransaction>();
    public DbSet<ReconciliationSession> Reconciliations => Set<ReconciliationSession>();
    public DbSet<FxRate> FxRates => Set<FxRate>();
    public DbSet<FxRevaluationLog> FxRevaluationLogs => Set<FxRevaluationLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        ArgumentNullException.ThrowIfNull(b);
        b.HasDefaultSchema(SchemaName);

        var filterEnabled = _tenantAccessor is not null;

        b.Entity<BankAccount>(e =>
        {
            e.ToTable("bank_accounts");
            e.HasKey(x => x.BankAccountId);
            e.Property(x => x.BankAccountId).HasColumnName("bank_account_id");
            e.Property(x => x.Code).HasColumnName("code").HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            e.Property(x => x.BankName).HasColumnName("bank_name").HasMaxLength(100).IsRequired();
            e.Property(x => x.AccountNumber).HasColumnName("account_number").HasMaxLength(64).IsRequired();
            e.Property(x => x.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();
            e.Property(x => x.LedgerAccount).HasColumnName("ledger_account").HasMaxLength(32).IsRequired();
            e.Property(x => x.IsActive).HasColumnName("is_active").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique().HasDatabaseName("ux_bank_accounts_tenant_code");

            if (filterEnabled) e.HasQueryFilter(x => x.TenantId == _tenantAccessor!.Current);
        });

        b.Entity<BankStatement>(e =>
        {
            e.ToTable("statements");
            e.HasKey(x => x.BankStatementId);
            e.Property(x => x.BankStatementId).HasColumnName("statement_id");
            e.Property(x => x.BankAccountId).HasColumnName("bank_account_id").IsRequired();
            e.Property(x => x.StatementDate).HasColumnName("statement_date").IsRequired();
            e.Property(x => x.PeriodStart).HasColumnName("period_start").IsRequired();
            e.Property(x => x.PeriodEnd).HasColumnName("period_end").IsRequired();
            e.Property(x => x.OpeningBalanceMinor).HasColumnName("opening_balance_minor").IsRequired();
            e.Property(x => x.ClosingBalanceMinor).HasColumnName("closing_balance_minor").IsRequired();
            e.Property(x => x.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();
            e.Property(x => x.SourceFileName).HasColumnName("source_file_name").HasMaxLength(500).IsRequired();
            e.Property(x => x.ParserName).HasColumnName("parser_name").HasMaxLength(64).IsRequired();
            e.Property(x => x.ImportedAt).HasColumnName("imported_at").IsRequired();
            e.Property(x => x.ImportedByUserId).HasColumnName("imported_by_user_id").IsRequired();
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.HasIndex(x => new { x.BankAccountId, x.PeriodStart, x.PeriodEnd }).HasDatabaseName("ix_statements_account_period");
            e.HasOne<BankAccount>().WithMany().HasForeignKey(x => x.BankAccountId).OnDelete(DeleteBehavior.Restrict);

            if (filterEnabled) e.HasQueryFilter(x => x.TenantId == _tenantAccessor!.Current);
        });

        b.Entity<BankTransaction>(e =>
        {
            e.ToTable("transactions");
            e.HasKey(x => x.BankTransactionId);
            e.Property(x => x.BankTransactionId).HasColumnName("transaction_id");
            e.Property(x => x.BankStatementId).HasColumnName("statement_id").IsRequired();
            e.Property(x => x.BankAccountId).HasColumnName("bank_account_id").IsRequired();
            e.Property(x => x.TransactionDate).HasColumnName("transaction_date").IsRequired();
            e.Property(x => x.ValueDate).HasColumnName("value_date");
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(500).IsRequired();
            e.Property(x => x.Reference).HasColumnName("reference").HasMaxLength(200);
            e.Property(x => x.Direction).HasColumnName("direction").HasConversion<short>().IsRequired();
            e.Property(x => x.AmountMinor).HasColumnName("amount_minor").IsRequired();
            e.Property(x => x.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();
            e.Property(x => x.MatchStatus).HasColumnName("match_status").HasConversion<short>().IsRequired();
            e.Property(x => x.MatchedToEntityId).HasColumnName("matched_to_entity_id");
            e.Property(x => x.MatchedToEntityType).HasColumnName("matched_to_entity_type").HasMaxLength(64);
            e.Property(x => x.MatchedAt).HasColumnName("matched_at");
            e.Property(x => x.MatchedByUserId).HasColumnName("matched_by_user_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.HasIndex(x => new { x.BankAccountId, x.TransactionDate }).HasDatabaseName("ix_transactions_account_date");
            e.HasIndex(x => new { x.TenantId, x.MatchStatus }).HasDatabaseName("ix_transactions_tenant_status");
            e.HasOne<BankStatement>().WithMany().HasForeignKey(x => x.BankStatementId).OnDelete(DeleteBehavior.Cascade);

            if (filterEnabled) e.HasQueryFilter(x => x.TenantId == _tenantAccessor!.Current);
        });

        b.Entity<ReconciliationSession>(e =>
        {
            e.ToTable("reconciliations");
            e.HasKey(x => x.ReconciliationSessionId);
            e.Property(x => x.ReconciliationSessionId).HasColumnName("reconciliation_id");
            e.Property(x => x.BankAccountId).HasColumnName("bank_account_id").IsRequired();
            e.Property(x => x.AsOfDate).HasColumnName("as_of_date").IsRequired();
            e.Property(x => x.BankBalanceMinor).HasColumnName("bank_balance_minor").IsRequired();
            e.Property(x => x.LedgerBalanceMinor).HasColumnName("ledger_balance_minor").IsRequired();
            e.Ignore(x => x.DifferenceMinor);
            e.Property(x => x.Status).HasColumnName("status").HasConversion<short>().IsRequired();
            e.Property(x => x.Notes).HasColumnName("notes").HasMaxLength(2000);
            e.Property(x => x.OpenedByUserId).HasColumnName("opened_by_user_id").IsRequired();
            e.Property(x => x.OpenedAt).HasColumnName("opened_at").IsRequired();
            e.Property(x => x.ClosedByUserId).HasColumnName("closed_by_user_id");
            e.Property(x => x.ClosedAt).HasColumnName("closed_at");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.HasIndex(x => new { x.BankAccountId, x.AsOfDate }).HasDatabaseName("ix_reconciliations_account_date");

            if (filterEnabled) e.HasQueryFilter(x => x.TenantId == _tenantAccessor!.Current);
        });

        b.Entity<FxRate>(e =>
        {
            e.ToTable("fx_rates");
            e.HasKey(x => x.FxRateId);
            e.Property(x => x.FxRateId).HasColumnName("fx_rate_id");
            e.Property(x => x.FromCurrency).HasColumnName("from_currency").HasMaxLength(3).IsRequired();
            e.Property(x => x.ToCurrency).HasColumnName("to_currency").HasMaxLength(3).IsRequired();
            // 18,8 covers exotic-currency precision (e.g. NGN→GHS at 0.01100) without surrendering banker-style
            // rounding accuracy; the conversion math itself is decimal in C# so no precision is lost in transit.
            e.Property(x => x.Rate).HasColumnName("rate").HasColumnType("numeric(18,8)").IsRequired();
            e.Property(x => x.AsOfDate).HasColumnName("as_of_date").IsRequired();
            e.Property(x => x.Source).HasColumnName("source").HasMaxLength(64).IsRequired();
            e.Property(x => x.RecordedAt).HasColumnName("recorded_at").IsRequired();
            e.Property(x => x.RecordedByUserId).HasColumnName("recorded_by_user_id").IsRequired();
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(x => x.ProviderPayloadJson).HasColumnName("provider_payload_json").HasColumnType("jsonb");
            // Re-imports for the same (tenant, pair, date) collapse onto the same row — manual edits and
            // nightly imports stay idempotent.
            e.HasIndex(x => new { x.TenantId, x.FromCurrency, x.ToCurrency, x.AsOfDate })
                .IsUnique()
                .HasDatabaseName("ux_fx_rates_tenant_pair_date");
            // Read-side query is "rates for pair X up to date Y, latest first" — covering index helps.
            e.HasIndex(x => new { x.TenantId, x.FromCurrency, x.ToCurrency, x.AsOfDate, x.Rate })
                .HasDatabaseName("ix_fx_rates_lookup");

            if (filterEnabled) e.HasQueryFilter(x => x.TenantId == _tenantAccessor!.Current);
        });

        b.Entity<FxRevaluationLog>(e =>
        {
            e.ToTable("fx_revaluation_log");
            e.HasKey(x => x.LogId);
            e.Property(x => x.LogId).HasColumnName("log_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(x => x.GlAccount).HasColumnName("gl_account").HasMaxLength(64).IsRequired();
            e.Property(x => x.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();
            e.Property(x => x.RevaluedAt).HasColumnName("revalued_at").IsRequired();
            e.Property(x => x.AsOfDate).HasColumnName("as_of_date").IsRequired();
            e.Property(x => x.PeriodId).HasColumnName("period_id").IsRequired();
            // Same precision as banking.fx_rates.rate so the rate stored here
            // is identical to what the converter returned, byte-for-byte.
            e.Property(x => x.RateUsed).HasColumnName("rate_used").HasColumnType("numeric(18,8)").IsRequired();
            e.Property(x => x.BalanceMinor).HasColumnName("balance_minor").IsRequired();
            e.Property(x => x.LedgerEventId).HasColumnName("ledger_event_id").IsRequired();

            // Per-period uniqueness — second run for the same (tenant, account,
            // ccy, period) collides at the DB layer; service idempotency catches
            // it earlier but the unique index is the safety net.
            e.HasIndex(x => new { x.TenantId, x.GlAccount, x.CurrencyCode, x.PeriodId })
                .IsUnique()
                .HasDatabaseName("ux_fx_revaluation_log_tenant_account_ccy_period");

            // Hot-path lookup: "what was the previous rate I used for this
            // (account, currency)?" — covering index keeps it index-only.
            e.HasIndex(x => new { x.TenantId, x.GlAccount, x.CurrencyCode, x.AsOfDate })
                .HasDatabaseName("ix_fx_revaluation_log_lookup");

            if (filterEnabled) e.HasQueryFilter(x => x.TenantId == _tenantAccessor!.Current);
        });
    }
}
