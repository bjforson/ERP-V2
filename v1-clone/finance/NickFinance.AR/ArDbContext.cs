using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Identity;

namespace NickFinance.AR;

/// <summary>
/// EF Core context for Accounts Receivable. Owns the <c>ar</c> schema
/// — customers, invoices, invoice lines, receipts.
/// </summary>
public class ArDbContext : DbContext
{
    public const string SchemaName = "ar";

    private readonly ITenantAccessor? _tenantAccessor;

    public ArDbContext(DbContextOptions<ArDbContext> options) : base(options) { }

    public ArDbContext(DbContextOptions<ArDbContext> options, ITenantAccessor? tenantAccessor)
        : base(options)
    {
        _tenantAccessor = tenantAccessor;
    }

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<ArInvoice> Invoices => Set<ArInvoice>();
    public DbSet<ArInvoiceLine> InvoiceLines => Set<ArInvoiceLine>();
    public DbSet<ArReceipt> Receipts => Set<ArReceipt>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        ArgumentNullException.ThrowIfNull(b);
        b.HasDefaultSchema(SchemaName);

        var filterEnabled = _tenantAccessor is not null;

        b.Entity<Customer>(e =>
        {
            e.ToTable("customers");
            e.HasKey(x => x.CustomerId);
            e.Property(x => x.CustomerId).HasColumnName("customer_id");
            e.Property(x => x.Code).HasColumnName("code").HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            e.Property(x => x.Tin).HasColumnName("tin").HasMaxLength(64);
            e.Property(x => x.IsVatRegistered).HasColumnName("is_vat_registered").IsRequired();
            e.Property(x => x.Email).HasColumnName("email").HasMaxLength(254);
            e.Property(x => x.Phone).HasColumnName("phone").HasMaxLength(32);
            e.Property(x => x.Address).HasColumnName("address").HasMaxLength(500);
            e.Property(x => x.ArControlAccount).HasColumnName("ar_control_account").HasMaxLength(32);
            e.Property(x => x.IsActive).HasColumnName("is_active").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique().HasDatabaseName("ux_customers_tenant_code");

            if (filterEnabled) e.HasQueryFilter(x => x.TenantId == _tenantAccessor!.Current);
        });

        b.Entity<ArInvoice>(e =>
        {
            e.ToTable("invoices");
            e.HasKey(x => x.ArInvoiceId);
            e.Property(x => x.ArInvoiceId).HasColumnName("invoice_id");
            e.Property(x => x.InvoiceNo).HasColumnName("invoice_no").HasMaxLength(64).IsRequired();
            e.Property(x => x.CustomerId).HasColumnName("customer_id").IsRequired();
            e.Property(x => x.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();
            e.Property(x => x.InvoiceDate).HasColumnName("invoice_date").IsRequired();
            e.Property(x => x.DueDate).HasColumnName("due_date").IsRequired();
            e.Property(x => x.Reference).HasColumnName("reference").HasMaxLength(200);
            e.Property(x => x.Status).HasColumnName("status").HasConversion<short>().IsRequired();
            e.Property(x => x.EvatIrn).HasColumnName("evat_irn").HasMaxLength(64);
            e.Property(x => x.EvatIssuedAt).HasColumnName("evat_issued_at");
            e.Property(x => x.SubtotalNetMinor).HasColumnName("subtotal_net_minor").IsRequired();
            e.Property(x => x.LeviesMinor).HasColumnName("levies_minor").IsRequired();
            e.Property(x => x.VatMinor).HasColumnName("vat_minor").IsRequired();
            e.Property(x => x.GrossMinor).HasColumnName("gross_minor").IsRequired();
            e.Property(x => x.PaidMinor).HasColumnName("paid_minor").IsRequired();
            e.Ignore(x => x.OutstandingMinor);
            e.Property(x => x.LedgerEventId).HasColumnName("ledger_event_id");
            e.Property(x => x.SourceModule).HasColumnName("source_module").HasMaxLength(64).IsRequired();
            e.Property(x => x.SourceEntityId).HasColumnName("source_entity_id").HasMaxLength(128);
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.Property(x => x.IssuedAt).HasColumnName("issued_at");
            e.Property(x => x.VoidedAt).HasColumnName("voided_at");
            e.Property(x => x.VoidReason).HasColumnName("void_reason").HasMaxLength(500);
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();

            e.HasIndex(x => new { x.TenantId, x.InvoiceNo }).IsUnique().HasDatabaseName("ux_invoices_tenant_invoiceno");
            e.HasIndex(x => new { x.TenantId, x.CustomerId, x.Status }).HasDatabaseName("ix_invoices_tenant_customer_status");

            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.ArInvoiceId).OnDelete(DeleteBehavior.Cascade);

            if (filterEnabled) e.HasQueryFilter(x => x.TenantId == _tenantAccessor!.Current);
        });

        b.Entity<ArInvoiceLine>(e =>
        {
            e.ToTable("invoice_lines");
            e.HasKey(x => x.ArInvoiceLineId);
            e.Property(x => x.ArInvoiceLineId).HasColumnName("invoice_line_id");
            e.Property(x => x.ArInvoiceId).HasColumnName("invoice_id").IsRequired();
            e.Property(x => x.LineNo).HasColumnName("line_no").IsRequired();
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(500).IsRequired();
            e.Property(x => x.NetAmountMinor).HasColumnName("net_amount_minor").IsRequired();
            e.Property(x => x.RevenueAccount).HasColumnName("revenue_account").HasMaxLength(32).IsRequired();
            e.Property(x => x.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();
            e.HasIndex(x => new { x.ArInvoiceId, x.LineNo }).IsUnique().HasDatabaseName("ux_invoice_lines_invoice_line");
        });

        b.Entity<ArReceipt>(e =>
        {
            e.ToTable("receipts");
            e.HasKey(x => x.ArReceiptId);
            e.Property(x => x.ArReceiptId).HasColumnName("receipt_id");
            e.Property(x => x.ArInvoiceId).HasColumnName("invoice_id").IsRequired();
            e.Property(x => x.ReceiptDate).HasColumnName("receipt_date").IsRequired();
            e.Property(x => x.AmountMinor).HasColumnName("amount_minor").IsRequired();
            e.Property(x => x.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();
            e.Property(x => x.CashAccount).HasColumnName("cash_account").HasMaxLength(32).IsRequired();
            e.Property(x => x.Reference).HasColumnName("reference").HasMaxLength(200);
            e.Property(x => x.RecordedByUserId).HasColumnName("recorded_by_user_id").IsRequired();
            e.Property(x => x.RecordedAt).HasColumnName("recorded_at").IsRequired();
            e.Property(x => x.LedgerEventId).HasColumnName("ledger_event_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.HasIndex(x => new { x.TenantId, x.ArInvoiceId, x.ReceiptDate }).HasDatabaseName("ix_receipts_tenant_invoice_date");
            e.HasOne<ArInvoice>().WithMany().HasForeignKey(x => x.ArInvoiceId).OnDelete(DeleteBehavior.Cascade);

            if (filterEnabled) e.HasQueryFilter(x => x.TenantId == _tenantAccessor!.Current);
        });
    }
}
