namespace NickFinance.WebApp.Components.Shared;

/// <summary>
/// One page of rows + the total row-count across all pages. Returned by
/// every <c>Fetch</c> delegate passed to <see cref="PagedTable{T,TF}"/>.
/// Total is the count BEFORE Skip/Take so the pager can render
/// "Showing 1–25 of 1,247".
/// </summary>
public sealed record PagedResult<TItem>(IReadOnlyList<TItem> Rows, int Total)
{
    public static PagedResult<TItem> Empty { get; } = new(Array.Empty<TItem>(), 0);
}
