using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NickERP.Platform.Audit;

/// <summary>
/// Helpers for building deterministic idempotency keys for
/// <see cref="Events.DomainEvent.IdempotencyKey"/>. Modules call these
/// rather than concatenating strings inline so the construction is
/// uniform across emitters.
/// </summary>
public static class IdempotencyKey
{
    /// <summary>
    /// Build a key from constituent parts. Joins with "<c>|</c>", trims, and
    /// returns a SHA-256 hex digest so the key has bounded length.
    /// </summary>
    /// <param name="parts">
    /// The values that uniquely identify the event. Common pattern:
    /// <c>(tenantId, eventType, entityId, occurredAt-rounded-to-second)</c>.
    /// Order matters — pick a stable order at the call site.
    /// </param>
    public static string From(params object?[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var sb = new StringBuilder();
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) sb.Append('|');
            sb.Append(NormalizePart(parts[i]));
        }
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Build a key for the canonical "this entity changed at this moment"
    /// case. Rounds <paramref name="occurredAt"/> to the second so two
    /// emitters that fire microseconds apart for the same logical event
    /// produce the same key.
    /// </summary>
    public static string ForEntityChange(long tenantId, string eventType, string entityType, string entityId, DateTimeOffset occurredAt)
    {
        var rounded = new DateTimeOffset(
            occurredAt.Year, occurredAt.Month, occurredAt.Day,
            occurredAt.Hour, occurredAt.Minute, occurredAt.Second,
            occurredAt.Offset);
        return From(tenantId, eventType, entityType, entityId,
            rounded.ToString("O", CultureInfo.InvariantCulture));
    }

    private static string NormalizePart(object? part)
    {
        return part switch
        {
            null => string.Empty,
            string s => s.Trim(),
            DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => part.ToString() ?? string.Empty
        };
    }
}
