using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Identity;

namespace NickFinance.AP;

public class ApDbContext : DbContext
{
    public const string SchemaName = "ap";

    private readonly ITenantAccessor? _tenantAccessor;

    public ApDbContext(DbContextOptions<ApDbContext> options) : base(options) { }

    public ApDbContext(DbContextOptions<ApDbContext> options, ITenantAccessor? tenantAccessor)
        : base(options)
    {
        _tenantAccessor = tenantAccessor;
    }

    public DbSet<Vendor> Vendors => Set<Vendor>();
    public DbSet<ApBill> Bills => Set<ApBill>();
    public DbSet<ApBillLine> BillLines => Set<ApBillLine>();
    public DbSet<ApPayment> Payments => Set<ApPayment>();
    public DbSet<WhtCertificate> WhtCertificates => Set<WhtCertificate>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        ArgumentNullException.ThrowIfNull(b);
        b.HasDefaultSchema(SchemaName);

        var filterEnabled = _tenantAccessor is not null;

        b.Entity<Vendor>(e =>
        {
            e.ToTable("vendors");
            e.HasKey(x => x.VendorId);
            e.Property(x => x.VendorId).HasColumnName("vendor_id");
            e.Property(x => x.Code).HasColumnName("code").HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            e.Property(x => x.Tin).HasColumnName("tin").HasMaxLength(64);
            e.Property(x => x.IsVatRegistered).HasColumnName("is_vat_registered").IsRequired();
            e.Property(x => x.DefaultWht).HasColumnName("default_wht").HasConversion<short>().IsRequired();
            e.Property(x => x.WhtExempt).HasColumnName("wht_exempt").IsRequired();
            e.Property(x => x.Email).HasColumnName("email").HasMaxLength(254);
            e.Property(x => x.Phone).HasColumnName("phone").HasMaxLength(32);
            e.Property(x => x.Address).HasColumnName("address").HasMaxLength(500);
            e.Property(x => x.MomoNumber).HasColumnName("momo_number").HasMaxLength(32);
            e.Property(x => x.MomoNetwork).HasColumnName("momo_network").HasMaxLength(16);
            e.Property(x => x.BankAccountNumber).HasColumnName("bank_account_number").HasMaxLength(64);
            e.Property(x => x.BankName).HasColumnName("bank_name").HasMaxLength(100);
            e.Property(x => x.DefaultExpenseAccount).HasColumnName("default_expense_account").HasMaxLength(32);
            e.Property(x => x.IsActive).HasColumnName("is_active").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique().HasDatabaseName("ux_vendors_tenant_code");

            if (filterEnabled) e.HasQueryFilter(x => x.TenantId == _tenantAccessor!.Current);
        });

        b.Entity<ApBill>(e =>
        {
            e.ToTable("bills");
            e.HasKey(x => x.ApBillId);
            e.Property(x => x.ApBillId).HasColumnName("bill_id");
            e.Property(x => x.BillNo).HasColumnName("bill_no").HasMaxLength(64).IsRequired();
            e.Property(x => x.VendorReference).HasColumnName("vendor_reference").HasMaxLength(128).IsRequired();
            e.Property(x => x.VendorId).HasColumnName("vendor_id").IsRequired();
            e.Property(x => x.BillDate).HasColumnName("bill_date").IsRequired();
            e.Property(x => x.DueDate).HasColumnName("due_date").IsRequired();
            e.Property(x => x.PoReference).HasColumnName("po_reference").HasMaxLength(128);
            e.Property(x => x.GrnReference).HasColumnName("grn_reference").HasMaxLength(128);
            e.Property(x => x.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();
            e.Property(x => x.Status).HasColumnName("status").HasConversion<short>().IsRequired();
            e.Property(x => x.SubtotalNetMinor).HasColumnName("subtotal_net_minor").IsRequired();
            e.Property(x => x.LeviesMinor).HasColumnName("levies_minor").IsRequired();
            e.Property(x => x.VatMinor).HasColumnName("vat_minor").IsRequired();
            e.Property(x => x.GrossMinor).HasColumnName("gross_minor").IsRequired();
            e.Property(x => x.WhtRateBp).HasColumnName("wht_rate_bp").IsRequired();
            e.Property(x => x.WhtMinor).HasColumnName("wht_minor").IsRequired();
            e.Property(x => x.PaidMinor).HasColumnName("paid_minor").IsRequired();
            e.Ignore(x => x.OutstandingMinor);
            e.Property(x => x.Notes).HasColumnName("notes").HasMaxLength(2000);
            e.Property(x => x.LedgerEventId).HasColumnName("ledger_event_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.Property(x => x.ApprovedAt).HasColumnName("approved_at");
            e.Property(x => x.ApprovedByUserId).HasColumnName("approved_by_user_id");
            e.Property(x => x.PaidAt).HasColumnName("paid_at");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.HasIndex(x => new { x.TenantId, x.BillNo }).IsUnique().HasDatabaseName("ux_bills_tenant_billno");
            e.HasIndex(x => new { x.TenantId, x.VendorId, x.Status }).HasDatabaseName("ix_bills_tenant_vendor_status");
            e.HasOne<Vendor>().WithMany().HasForeignKey(x => x.VendorId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.ApBillId).OnDelete(DeleteBehavior.Cascade);

            if (filterEnabled) e.HasQueryFilter(x => x.TenantId == _tenantAccessor!.Current);
        });

        b.Entity<ApBillLine>(e =>
        {
            e.ToTable("bill_lines");
            e.HasKey(x => x.ApBillLineId);
            e.Property(x => x.ApBillLineId).HasColumnName("bill_line_id");
            e.Property(x => x.ApBillId).HasColumnName("bill_id").IsRequired();
            e.Property(x => x.LineNo).HasColumnName("line_no").IsRequired();
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(500).IsRequired();
            e.Property(x => x.NetAmountMinor).HasColumnName("net_amount_minor").IsRequired();
            e.Property(x => x.ExpenseAccount).HasColumnName("expense_account").HasMaxLength(32).IsRequired();
            e.Property(x => x.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();
            e.HasIndex(x => new { x.ApBillId, x.LineNo }).IsUnique().HasDatabaseName("ux_bill_lines_bill_line");
        });

        b.Entity<ApPayment>(e =>
        {
            e.ToTable("payments");
            e.HasKey(x => x.ApPaymentId);
            e.Property(x => x.ApPaymentId).HasColumnName("payment_id");
            e.Property(x => x.ApBillId).HasColumnName("bill_id").IsRequired();
            e.Property(x => x.PaymentDate).HasColumnName("payment_date").IsRequired();
            e.Property(x => x.AmountMinor).HasColumnName("amount_minor").IsRequired();
            e.Property(x => x.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();
            e.Property(x => x.PaymentRail).HasColumnName("payment_rail").HasMaxLength(32).IsRequired();
            e.Property(x => x.CashAccount).HasColumnName("cash_account").HasMaxLength(32).IsRequired();
            e.Property(x => x.RailReference).HasColumnName("rail_reference").HasMaxLength(128);
            e.Property(x => x.PaymentRunId).HasColumnName("payment_run_id").IsRequired();
            e.Property(x => x.RecordedByUserId).HasColumnName("recorded_by_user_id").IsRequired();
            e.Property(x => x.RecordedAt).HasColumnName("recorded_at").IsRequired();
            e.Property(x => x.LedgerEventId).HasColumnName("ledger_event_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.HasIndex(x => new { x.TenantId, x.PaymentRunId }).HasDatabaseName("ix_payments_tenant_run");
            e.HasOne<ApBill>().WithMany().HasForeignKey(x => x.ApBillId).OnDelete(DeleteBehavior.Cascade);

            if (filterEnabled) e.HasQueryFilter(x => x.TenantId == _tenantAccessor!.Current);
        });

        b.Entity<WhtCertificate>(e =>
        {
            e.ToTable("wht_certificates");
            e.HasKey(x => x.WhtCertificateId);
            e.Property(x => x.WhtCertificateId).HasColumnName("wht_certificate_id");
            e.Property(x => x.CertificateNo).HasColumnName("certificate_no").HasMaxLength(64).IsRequired();
            e.Property(x => x.VendorId).HasColumnName("vendor_id").IsRequired();
            e.Property(x => x.ApBillId).HasColumnName("bill_id");
            e.Property(x => x.ApPaymentId).HasColumnName("payment_id");
            e.Property(x => x.IssueDate).HasColumnName("issue_date").IsRequired();
            e.Property(x => x.GrossPaidMinor).HasColumnName("gross_paid_minor").IsRequired();
            e.Property(x => x.WhtDeductedMinor).HasColumnName("wht_deducted_minor").IsRequired();
            e.Property(x => x.WhtRate).HasColumnName("wht_rate").HasPrecision(6, 4).IsRequired();
            e.Property(x => x.TransactionType).HasColumnName("transaction_type").HasConversion<short>().IsRequired();
            e.Property(x => x.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.HasIndex(x => new { x.TenantId, x.CertificateNo }).IsUnique().HasDatabaseName("ux_wht_certs_tenant_no");
            e.HasIndex(x => new { x.TenantId, x.VendorId, x.IssueDate }).HasDatabaseName("ix_wht_certs_vendor_date");

            if (filterEnabled) e.HasQueryFilter(x => x.TenantId == _tenantAccessor!.Current);
        });
    }
}
