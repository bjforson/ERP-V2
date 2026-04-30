namespace NickFinance.Coa;

/// <summary>
/// One row in the chart of accounts. <see cref="Code"/> is the natural key
/// every ledger line's <c>account_code</c> refers to — Ghana SME numeric
/// convention (<c>1010</c>, <c>2110</c>, <c>4010</c>) documented in
/// <c>FINANCE_KERNEL.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// The Ledger uses a <em>dimension-based</em> model: site, project and
/// cost centre are columns on each ledger line, not suffixes baked into
/// the <see cref="Code"/>. So the chart stays small and bounded —
/// typically &lt;100 rows for an SME — even with six sites and many
/// projects.
/// </para>
/// <para>
/// <see cref="IsControl"/> distinguishes accounts that should only be
/// posted to by code (e.g. <c>2110 VAT output payable</c> by the tax
/// engine, <c>1100 Trade receivables</c> by the AR module) from accounts
/// any user-driven journal can hit. The Ledger kernel does not enforce
/// this — modules do — but the flag is exposed so the UI can warn.
/// </para>
/// </remarks>
public class Account
{
    /// <summary>Stable surrogate id. Foreign keys point here, not at <see cref="Code"/>.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Account code, unique within a tenant. Numeric Ghana SME convention; up to 32 chars.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Human-readable name shown on reports.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The fundamental category. Drives normal-balance side and report placement.</summary>
    public AccountType Type { get; set; }

    /// <summary>Optional parent account code for roll-up grouping. <see langword="null"/> for top-level.</summary>
    public string? ParentCode { get; set; }

    /// <summary>
    /// Currency the account is denominated in. Most accounts will be
    /// <c>GHS</c>; bank/MoMo USD accounts will be <c>USD</c>; some control
    /// accounts (e.g. AR, AP) span currencies — encoded as <c>"*"</c> for
    /// "any currency, but a single line is still single-currency".
    /// </summary>
    public string CurrencyCode { get; set; } = "GHS";

    /// <summary><c>true</c> when only the kernel / module code should post here. Advisory; not enforced by the writer.</summary>
    public bool IsControl { get; set; }

    /// <summary>
    /// <c>false</c> = retired. The Ledger writer does NOT enforce account
    /// existence/active-ness — that's a module concern. Modules that want
    /// CoA validation look up <see cref="Code"/> here before posting.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Free-text description for the chart-of-accounts UI.</summary>
    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Multi-tenant isolation key. Aligns with Platform.Tenancy contract. Tenant 1 = Nick TC-Scan.</summary>
    public long TenantId { get; set; } = 1;
}
