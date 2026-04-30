namespace NickFinance.Coa;

/// <summary>
/// The five canonical account types for double-entry bookkeeping. Each type
/// has a fixed normal balance (debit or credit) which the kernel uses to
/// project balances correctly and to drive the report sign convention.
/// </summary>
public enum AccountType
{
    /// <summary>
    /// Things the entity owns or is owed. Normal balance: debit. Lives on
    /// the balance sheet. Codes 1xxx by convention.
    /// </summary>
    Asset = 1,

    /// <summary>
    /// Obligations the entity owes. Normal balance: credit. Lives on
    /// the balance sheet. Codes 2xxx by convention.
    /// </summary>
    Liability = 2,

    /// <summary>
    /// Owners' residual interest. Normal balance: credit. Lives on
    /// the balance sheet. Codes 3xxx by convention.
    /// </summary>
    Equity = 3,

    /// <summary>
    /// Earnings recognised in the period. Normal balance: credit. Lives
    /// on the P&amp;L. Codes 4xxx by convention.
    /// </summary>
    Income = 4,

    /// <summary>
    /// Costs recognised in the period. Normal balance: debit. Lives on
    /// the P&amp;L. Codes 5xxx-8xxx by convention (cost of services,
    /// operating expenses, finance costs, tax expense).
    /// </summary>
    Expense = 5
}

/// <summary>The side a debit/credit posting represents an *increase* on for a given account type.</summary>
public enum NormalBalance
{
    /// <summary>Asset and Expense accounts increase on debit.</summary>
    Debit,

    /// <summary>Liability, Equity, Income accounts increase on credit.</summary>
    Credit
}

/// <summary>Helpers around <see cref="AccountType"/>.</summary>
public static class AccountTypeExtensions
{
    /// <summary>The normal balance side for the type — debit for Asset/Expense, credit for the rest.</summary>
    public static NormalBalance GetNormalBalance(this AccountType type) => type switch
    {
        AccountType.Asset => NormalBalance.Debit,
        AccountType.Expense => NormalBalance.Debit,
        AccountType.Liability => NormalBalance.Credit,
        AccountType.Equity => NormalBalance.Credit,
        AccountType.Income => NormalBalance.Credit,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown AccountType.")
    };

    /// <summary>Whether the account belongs on the Balance Sheet (Asset/Liability/Equity).</summary>
    public static bool IsBalanceSheet(this AccountType type) =>
        type is AccountType.Asset or AccountType.Liability or AccountType.Equity;

    /// <summary>Whether the account belongs on the P&amp;L (Income/Expense).</summary>
    public static bool IsProfitAndLoss(this AccountType type) =>
        type is AccountType.Income or AccountType.Expense;
}
