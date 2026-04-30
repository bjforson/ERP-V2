using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Identity;
using NickFinance.PettyCash.Approvals;
using NickFinance.PettyCash.Budgets;
using NickFinance.PettyCash.CashCounts;
using NickFinance.PettyCash.Receipts;
using NickFinance.PettyCash.Recurring;

namespace NickFinance.PettyCash;

/// <summary>
/// EF Core context for the Petty Cash module. Owns the <c>petty_cash</c>
/// schema. The Ledger lives in a separate <see cref="Ledger.LedgerDbContext"/>
/// in the <c>finance</c> schema of the same physical database — modules
/// post to the ledger via <see cref="Ledger.ILedgerWriter"/>, never reaching
/// in directly.
/// </summary>
public class PettyCashDbContext : DbContext
{
    public const string SchemaName = "petty_cash";

    private readonly ITenantAccessor? _tenantAccessor;

    public PettyCashDbContext(DbContextOptions<PettyCashDbContext> options) : base(options) { }

    public PettyCashDbContext(DbContextOptions<PettyCashDbContext> options, ITenantAccessor? tenantAccessor)
        : base(options)
    {
        _tenantAccessor = tenantAccessor;
    }

    public DbSet<Float> Floats => Set<Float>();
    public DbSet<Voucher> Vouchers => Set<Voucher>();
    public DbSet<VoucherLineItem> VoucherLines => Set<VoucherLineItem>();
    public DbSet<VoucherApproval> VoucherApprovals => Set<VoucherApproval>();
    public DbSet<ApprovalDelegation> ApprovalDelegations => Set<ApprovalDelegation>();
    public DbSet<VoucherReceipt> VoucherReceipts => Set<VoucherReceipt>();
    public DbSet<CashCount> CashCounts => Set<CashCount>();
    public DbSet<Budget> Budgets => Set<Budget>();
    public DbSet<RecurringVoucherTemplate> RecurringVoucherTemplates => Set<RecurringVoucherTemplate>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        ArgumentNullException.ThrowIfNull(b);

        b.HasDefaultSchema(SchemaName);

        var filterEnabled = _tenantAccessor is not null;

        b.Entity<Float>(e =>
        {
            e.ToTable("floats");
            e.HasKey(x => x.FloatId);
            e.Property(x => x.FloatId).HasColumnName("float_id");
            e.Property(x => x.SiteId).HasColumnName("site_id").IsRequired();
            e.Property(x => x.CustodianUserId).HasColumnName("custodian_user_id").IsRequired();
            e.Property(x => x.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();
            e.Property(x => x.FloatAmountMinor).HasColumnName("float_amount_minor").IsRequired();
            e.Ignore(x => x.FloatAmount);
            e.Property(x => x.ReplenishThresholdPct).HasColumnName("replenish_threshold_pct").IsRequired();
            e.Property(x => x.IsActive).HasColumnName("is_active").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
            e.Property(x => x.ClosedAt).HasColumnName("closed_at");
            e.Property(x => x.ClosedByUserId).HasColumnName("closed_by_user_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();

            // One active float per (site, currency) — closed floats can stack underneath.
            e.HasIndex(x => new { x.TenantId, x.SiteId, x.CurrencyCode, x.IsActive })
             .HasFilter("\"is_active\" = TRUE")
             .IsUnique()
             .HasDatabaseName("ux_floats_active_per_site_currency");

            if (filterEnabled) e.HasQueryFilter(x => x.TenantId == _tenantAccessor!.Current);
        });

        b.Entity<Voucher>(e =>
        {
            e.ToTable("vouchers");
            e.HasKey(x => x.VoucherId);
            e.Property(x => x.VoucherId).HasColumnName("voucher_id");
            e.Property(x => x.VoucherNo).HasColumnName("voucher_no").HasMaxLength(64).IsRequired();
            e.Property(x => x.FloatId).HasColumnName("float_id").IsRequired();
            e.Property(x => x.RequesterUserId).HasColumnName("requester_user_id").IsRequired();
            e.Property(x => x.Category).HasColumnName("category").HasConversion<short>().IsRequired();
            e.Property(x => x.Purpose).HasColumnName("purpose").HasMaxLength(500).IsRequired();
            e.Property(x => x.AmountRequestedMinor).HasColumnName("amount_requested_minor").IsRequired();
            e.Property(x => x.AmountApprovedMinor).HasColumnName("amount_approved_minor");
            e.Property(x => x.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();
            e.Property(x => x.Status).HasColumnName("status").HasConversion<short>().IsRequired();
            e.Property(x => x.PayeeName).HasColumnName("payee_name").HasMaxLength(200);
            e.Property(x => x.PayeeMomoNumber).HasColumnName("payee_momo_number").HasMaxLength(32);
            e.Property(x => x.PayeeMomoNetwork).HasColumnName("payee_momo_network").HasMaxLength(16);
            e.Property(x => x.ProjectCode).HasColumnName("project_code").HasMaxLength(64);
            e.Property(x => x.DisbursementChannel).HasColumnName("disbursement_channel").HasMaxLength(64);
            e.Property(x => x.DisbursementReference).HasColumnName("disbursement_reference").HasMaxLength(128);
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.Property(x => x.SubmittedAt).HasColumnName("submitted_at");
            e.Property(x => x.DecidedAt).HasColumnName("decided_at");
            e.Property(x => x.DisbursedAt).HasColumnName("disbursed_at");
            e.Property(x => x.DecidedByUserId).HasColumnName("decided_by_user_id");
            e.Property(x => x.DecisionComment).HasColumnName("decision_comment").HasMaxLength(1000);
            e.Property(x => x.DisbursedByUserId).HasColumnName("disbursed_by_user_id");
            e.Property(x => x.LedgerEventId).HasColumnName("ledger_event_id");
            e.Property(x => x.TaxTreatment).HasColumnName("tax_treatment").HasConversion<short>().IsRequired();
            e.Property(x => x.WhtTreatment).HasColumnName("wht_treatment").HasConversion<short>().IsRequired();
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();

            e.HasIndex(x => new { x.TenantId, x.VoucherNo })
             .IsUnique()
             .HasDatabaseName("ux_vouchers_tenant_voucher_no");

            e.HasIndex(x => new { x.TenantId, x.FloatId, x.Status })
             .HasDatabaseName("ix_vouchers_tenant_float_status");

            e.HasIndex(x => new { x.TenantId, x.RequesterUserId, x.Status })
             .HasDatabaseName("ix_vouchers_tenant_requester_status");

            e.HasOne<Float>()
             .WithMany()
             .HasForeignKey(x => x.FloatId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasMany(x => x.Lines)
             .WithOne()
             .HasForeignKey(l => l.VoucherId)
             .OnDelete(DeleteBehavior.Cascade);

            if (filterEnabled) e.HasQueryFilter(x => x.TenantId == _tenantAccessor!.Current);
        });

        b.Entity<VoucherLineItem>(e =>
        {
            e.ToTable("voucher_line_items");
            e.HasKey(x => x.VoucherLineId);
            e.Property(x => x.VoucherLineId).HasColumnName("voucher_line_id");
            e.Property(x => x.VoucherId).HasColumnName("voucher_id").IsRequired();
            e.Property(x => x.LineNo).HasColumnName("line_no").IsRequired();
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(500).IsRequired();
            e.Property(x => x.GrossAmountMinor).HasColumnName("gross_amount_minor").IsRequired();
            e.Property(x => x.GlAccount).HasColumnName("gl_account").HasMaxLength(32).IsRequired();
            e.Property(x => x.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();

            e.HasIndex(x => new { x.VoucherId, x.LineNo })
             .IsUnique()
             .HasDatabaseName("ux_voucher_lines_voucher_lineno");
        });

        // -------------------------------------------------------------------
        // voucher_approvals — one row per step, ordered by step_no.
        // -------------------------------------------------------------------
        b.Entity<VoucherApproval>(e =>
        {
            e.ToTable("voucher_approvals");
            e.HasKey(x => x.VoucherApprovalId);
            e.Property(x => x.VoucherApprovalId).HasColumnName("voucher_approval_id");
            e.Property(x => x.VoucherId).HasColumnName("voucher_id").IsRequired();
            e.Property(x => x.StepNo).HasColumnName("step_no").IsRequired();
            e.Property(x => x.Role).HasColumnName("role").HasMaxLength(64).IsRequired();
            e.Property(x => x.AssignedToUserId).HasColumnName("assigned_to_user_id").IsRequired();
            e.Property(x => x.Decision).HasColumnName("decision").HasConversion<short>().IsRequired();
            e.Property(x => x.DecidedByUserId).HasColumnName("decided_by_user_id");
            e.Property(x => x.Comment).HasColumnName("comment").HasMaxLength(1000);
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.Property(x => x.DecidedAt).HasColumnName("decided_at");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();

            // Step is not unique on (voucher, step_no) any more — parallel
            // steps generate multiple rows with the same step_no, one per
            // role / assignee. (voucher, step_no, assigned_to) IS unique
            // because each assignee can only get one row per step (the
            // escalator inserts at the same step_no but with a NEW
            // assignee and a NEW row — so still unique).
            e.HasIndex(x => new { x.VoucherId, x.StepNo, x.AssignedToUserId })
             .IsUnique()
             .HasDatabaseName("ux_voucher_approvals_voucher_step_assignee");

            e.HasIndex(x => new { x.VoucherId, x.StepNo })
             .HasDatabaseName("ix_voucher_approvals_voucher_step");

            e.HasIndex(x => new { x.TenantId, x.AssignedToUserId, x.Decision })
             .HasDatabaseName("ix_voucher_approvals_assignee_decision");

            e.HasOne<Voucher>()
             .WithMany()
             .HasForeignKey(x => x.VoucherId)
             .OnDelete(DeleteBehavior.Cascade);

            if (filterEnabled) e.HasQueryFilter(x => x.TenantId == _tenantAccessor!.Current);
        });

        // -------------------------------------------------------------------
        // approval_delegations — covering-while-away assignments
        // -------------------------------------------------------------------
        b.Entity<ApprovalDelegation>(e =>
        {
            e.ToTable("approval_delegations");
            e.HasKey(x => x.ApprovalDelegationId);
            e.Property(x => x.ApprovalDelegationId).HasColumnName("approval_delegation_id");
            e.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
            e.Property(x => x.DelegateUserId).HasColumnName("delegate_user_id").IsRequired();
            e.Property(x => x.ValidFromUtc).HasColumnName("valid_from_utc").IsRequired();
            e.Property(x => x.ValidUntilUtc).HasColumnName("valid_until_utc").IsRequired();
            e.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(500);
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();

            e.HasIndex(x => new { x.TenantId, x.UserId, x.ValidFromUtc, x.ValidUntilUtc })
             .HasDatabaseName("ix_approval_delegations_user_window");

            if (filterEnabled) e.HasQueryFilter(x => x.TenantId == _tenantAccessor!.Current);
        });

        // -------------------------------------------------------------------
        // voucher_receipts
        // -------------------------------------------------------------------
        b.Entity<VoucherReceipt>(e =>
        {
            e.ToTable("voucher_receipts");
            e.HasKey(x => x.VoucherReceiptId);
            e.Property(x => x.VoucherReceiptId).HasColumnName("voucher_receipt_id");
            e.Property(x => x.VoucherId).HasColumnName("voucher_id").IsRequired();
            e.Property(x => x.Ordinal).HasColumnName("ordinal").IsRequired();
            e.Property(x => x.FilePath).HasColumnName("file_path").HasMaxLength(1000).IsRequired();
            e.Property(x => x.Sha256).HasColumnName("sha256").HasMaxLength(64).IsRequired();
            e.Property(x => x.ApproximateHash).HasColumnName("approximate_hash").HasMaxLength(64).IsRequired();
            e.Property(x => x.ContentType).HasColumnName("content_type").HasMaxLength(100).IsRequired();
            e.Property(x => x.FileSizeBytes).HasColumnName("file_size_bytes").IsRequired();
            e.Property(x => x.OcrVendor).HasColumnName("ocr_vendor").HasMaxLength(64);
            e.Property(x => x.OcrAmountMinor).HasColumnName("ocr_amount_minor");
            e.Property(x => x.OcrDate).HasColumnName("ocr_date");
            e.Property(x => x.OcrRawText).HasColumnName("ocr_raw_text");
            e.Property(x => x.OcrConfidence).HasColumnName("ocr_confidence");
            e.Property(x => x.UploadedByUserId).HasColumnName("uploaded_by_user_id").IsRequired();
            e.Property(x => x.UploadedAt).HasColumnName("uploaded_at").IsRequired();
            e.Property(x => x.GpsLatitude).HasColumnName("gps_latitude").HasPrecision(9, 6);
            e.Property(x => x.GpsLongitude).HasColumnName("gps_longitude").HasPrecision(9, 6);
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();

            e.HasIndex(x => new { x.VoucherId, x.Ordinal })
             .IsUnique()
             .HasDatabaseName("ux_voucher_receipts_voucher_ordinal");
            e.HasIndex(x => new { x.TenantId, x.Sha256 })
             .HasDatabaseName("ix_voucher_receipts_tenant_sha");
            e.HasIndex(x => new { x.TenantId, x.ApproximateHash })
             .HasDatabaseName("ix_voucher_receipts_tenant_approx");
            e.HasOne<Voucher>().WithMany().HasForeignKey(x => x.VoucherId).OnDelete(DeleteBehavior.Cascade);

            if (filterEnabled) e.HasQueryFilter(x => x.TenantId == _tenantAccessor!.Current);
        });

        // -------------------------------------------------------------------
        // cash_counts
        // -------------------------------------------------------------------
        b.Entity<CashCount>(e =>
        {
            e.ToTable("cash_counts");
            e.HasKey(x => x.CashCountId);
            e.Property(x => x.CashCountId).HasColumnName("cash_count_id");
            e.Property(x => x.FloatId).HasColumnName("float_id").IsRequired();
            e.Property(x => x.CountedByUserId).HasColumnName("counted_by_user_id").IsRequired();
            e.Property(x => x.WitnessUserId).HasColumnName("witness_user_id");
            e.Property(x => x.CountedAt).HasColumnName("counted_at").IsRequired();
            e.Property(x => x.PhysicalAmountMinor).HasColumnName("physical_amount_minor").IsRequired();
            e.Property(x => x.SystemAmountMinor).HasColumnName("system_amount_minor").IsRequired();
            e.Property(x => x.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();
            e.Property(x => x.VarianceReason).HasColumnName("variance_reason").HasMaxLength(1000);
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Ignore(x => x.VarianceMinor);

            e.HasIndex(x => new { x.FloatId, x.CountedAt }).HasDatabaseName("ix_cash_counts_float_when");
            e.HasOne<Float>().WithMany().HasForeignKey(x => x.FloatId).OnDelete(DeleteBehavior.Cascade);

            if (filterEnabled) e.HasQueryFilter(x => x.TenantId == _tenantAccessor!.Current);
        });

        // -------------------------------------------------------------------
        // budgets
        // -------------------------------------------------------------------
        b.Entity<Budget>(e =>
        {
            e.ToTable("budgets");
            e.HasKey(x => x.BudgetId);
            e.Property(x => x.BudgetId).HasColumnName("budget_id");
            e.Property(x => x.Scope).HasColumnName("scope").HasConversion<short>().IsRequired();
            e.Property(x => x.ScopeKey).HasColumnName("scope_key").HasMaxLength(64).IsRequired();
            e.Property(x => x.PeriodStart).HasColumnName("period_start").IsRequired();
            e.Property(x => x.PeriodEnd).HasColumnName("period_end").IsRequired();
            e.Property(x => x.AmountMinor).HasColumnName("amount_minor").IsRequired();
            e.Property(x => x.ConsumedMinor).HasColumnName("consumed_minor").IsRequired();
            e.Property(x => x.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();
            e.Property(x => x.AlertThresholdPct).HasColumnName("alert_threshold_pct").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();

            e.HasIndex(x => new { x.TenantId, x.Scope, x.ScopeKey, x.PeriodStart, x.PeriodEnd })
             .HasDatabaseName("ix_budgets_tenant_scope_period");

            if (filterEnabled) e.HasQueryFilter(x => x.TenantId == _tenantAccessor!.Current);
        });

        // -------------------------------------------------------------------
        // recurring_voucher_templates (v1.2)
        // -------------------------------------------------------------------
        b.Entity<RecurringVoucherTemplate>(e =>
        {
            e.ToTable("recurring_voucher_templates");
            e.HasKey(x => x.RecurringVoucherTemplateId);
            e.Property(x => x.RecurringVoucherTemplateId).HasColumnName("recurring_voucher_template_id");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            e.Property(x => x.FloatId).HasColumnName("float_id").IsRequired();
            e.Property(x => x.RequesterUserId).HasColumnName("requester_user_id").IsRequired();
            e.Property(x => x.Category).HasColumnName("category").HasConversion<short>().IsRequired();
            e.Property(x => x.Purpose).HasColumnName("purpose").HasMaxLength(500).IsRequired();
            e.Property(x => x.AmountMinor).HasColumnName("amount_minor").IsRequired();
            e.Property(x => x.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();
            e.Property(x => x.Frequency).HasColumnName("frequency").HasConversion<short>().IsRequired();
            e.Property(x => x.StartDate).HasColumnName("start_date").IsRequired();
            e.Property(x => x.EndDate).HasColumnName("end_date");
            e.Property(x => x.LastFiredOn).HasColumnName("last_fired_on");
            e.Property(x => x.IsActive).HasColumnName("is_active").IsRequired();
            e.Property(x => x.PayeeName).HasColumnName("payee_name").HasMaxLength(200);
            e.Property(x => x.ProjectCode).HasColumnName("project_code").HasMaxLength(64);
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.HasIndex(x => new { x.TenantId, x.IsActive, x.Frequency }).HasDatabaseName("ix_recurring_tenant_active_freq");

            if (filterEnabled) e.HasQueryFilter(x => x.TenantId == _tenantAccessor!.Current);
        });
    }
}
