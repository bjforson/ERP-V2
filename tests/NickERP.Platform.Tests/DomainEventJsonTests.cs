using System.Text.Json;
using NickERP.Platform.Audit.Events;

namespace NickERP.Platform.Tests;

/// <summary>
/// G1 #1 — round-trip <see cref="decimal"/>s through a
/// <see cref="DomainEvent"/> payload using
/// <see cref="DomainEventJson.Options"/> and assert lossless behaviour.
/// </summary>
public class DomainEventJsonTests
{
    public sealed record MoneyShape(decimal Amount, decimal? Tax, string CurrencyCode);

    [Fact]
    public void Decimal_round_trip_via_DomainEventJson_Options_is_lossless_on_long_decimals()
    {
        // The classic foot-gun: decimal.MaxValue / 7m yields a value with
        // 28 significant digits — past the safe range of double.
        var original = new MoneyShape(
            Amount: decimal.MaxValue / 7m,
            Tax: 0.000_000_000_000_000_001m,
            CurrencyCode: "GHS");

        var json = JsonSerializer.SerializeToElement(original, DomainEventJson.Options);
        var rebuilt = JsonSerializer.Deserialize<MoneyShape>(json.GetRawText(), DomainEventJson.Options);

        rebuilt.Should().NotBeNull();
        rebuilt!.Amount.Should().Be(original.Amount);
        rebuilt.Tax.Should().Be(original.Tax);
        rebuilt.CurrencyCode.Should().Be("GHS");
    }

    [Fact]
    public void Decimal_serialised_through_DomainEventJson_Options_is_a_JSON_string()
    {
        // The contract: decimals go to the wire as JSON strings, never
        // numeric literals — that's how we dodge the JsonElement-via-double
        // pitfall on later reads.
        var raw = JsonSerializer.Serialize(new MoneyShape(123.45m, null, "USD"), DomainEventJson.Options);
        raw.Should().Contain("\"Amount\":\"123.45\"");
        raw.Should().Contain("\"Tax\":null");
    }

    [Fact]
    public void Reading_a_decimal_written_as_JSON_number_still_succeeds()
    {
        // Tolerance for non-canonical writers: we accept JSON numbers OR
        // strings on read. Old payloads written before G1 stay readable.
        var raw = "{\"Amount\":42.99,\"Tax\":null,\"CurrencyCode\":\"GHS\"}";
        var rebuilt = JsonSerializer.Deserialize<MoneyShape>(raw, DomainEventJson.Options);
        rebuilt!.Amount.Should().Be(42.99m);
        rebuilt.CurrencyCode.Should().Be("GHS");
    }

    [Fact]
    public void Round_trip_through_JsonElement_does_not_demote_decimal_to_double()
    {
        // The bug class this options instance exists to prevent: a writer
        // that emits decimal as a JSON number, then a reader that drops
        // it through a JsonElement and re-deserialises — drift on values
        // past double's ~15-digit precision.
        var amount = 12345678901234567.89m; // 19 sig digits — past double's safe range.
        var element = JsonSerializer.SerializeToElement(new { Amount = amount }, DomainEventJson.Options);
        var raw = element.GetRawText();
        // decimal-as-string survives: the raw JSON is the canonical form.
        raw.Should().Contain("\"12345678901234567.89\"");

        var holder = JsonSerializer.Deserialize<MoneyShape>(
            "{\"Amount\":\"" + amount.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\",\"Tax\":null,\"CurrencyCode\":\"GHS\"}",
            DomainEventJson.Options);
        holder!.Amount.Should().Be(amount);
    }
}
