using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NickERP.Platform.Audit.Events;

/// <summary>
/// Canonical <see cref="JsonSerializerOptions"/> for <see cref="DomainEvent"/>
/// payloads. Use this for every <c>JsonSerializer.Serialize</c> /
/// <c>JsonSerializer.Deserialize</c> call that produces or consumes a
/// <see cref="DomainEvent.Payload"/> JSON document — the platform's
/// promise of lossless decimal round-trip rests on it.
/// </summary>
/// <remarks>
/// <para>
/// The defining concern is <see cref="decimal"/>. <c>System.Text.Json</c>'s
/// default behaviour writes <c>decimal</c> as a JSON number; round-tripping
/// through <c>JsonElement</c> / generic readers later can demote those
/// numbers to <see cref="double"/>, losing precision (cents vanish on
/// values past ~15 significant figures, and even smaller values pick up
/// representation drift). NickFinance's money-shaped events would surface
/// the bug as silent off-by-one-cent posting errors.
/// </para>
/// <para>
/// This options instance plugs in <see cref="DecimalAsStringConverter"/>
/// + the matching nullable variant, forcing every <c>decimal</c> property
/// to (de)serialise as a JSON string in invariant culture
/// (<c>"123.45"</c>, never <c>123.45</c> or <c>"123,45"</c>). Consumers
/// that already store the payload as a <see cref="JsonElement"/> are
/// covered transparently — the rule is "always write through this options
/// instance and you can never lose cents."
/// </para>
/// <para>
/// Other settings: invariant culture is implicit (the converter parses /
/// formats without consulting the current thread's culture), property
/// names stay raw (no camel-case re-mapping — event payloads are produced
/// by domain code, not REST DTOs), trailing-comma + comment tolerance is
/// off (audit events are machine-emitted; humans don't hand-edit them).
/// </para>
/// </remarks>
public static class DomainEventJson
{
    /// <summary>
    /// Singleton, frozen on first access. Mutating is not supported —
    /// derive a copy via <c>new JsonSerializerOptions(DomainEventJson.Options)</c>
    /// if a caller has special needs.
    /// </summary>
    public static readonly JsonSerializerOptions Options = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var opts = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = false,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
        };
        opts.Converters.Add(new DecimalAsStringConverter());
        opts.Converters.Add(new NullableDecimalAsStringConverter());
        return opts;
    }

    /// <summary>
    /// Force <see cref="decimal"/> through a JSON string in invariant
    /// culture so a round-trip through <c>JsonElement</c> never demotes
    /// to <see cref="double"/>.
    /// </summary>
    public sealed class DecimalAsStringConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // Accept either a JSON string OR a JSON number — readers may
            // see a value originally written by a non-canonical writer.
            if (reader.TokenType == JsonTokenType.String)
            {
                var s = reader.GetString();
                return decimal.Parse(s!, NumberStyles.Number, CultureInfo.InvariantCulture);
            }
            if (reader.TokenType == JsonTokenType.Number)
            {
                return reader.GetDecimal();
            }
            throw new JsonException(
                $"Expected JSON string or number for decimal, got {reader.TokenType}.");
        }

        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Nullable companion to <see cref="DecimalAsStringConverter"/>.</summary>
    public sealed class NullableDecimalAsStringConverter : JsonConverter<decimal?>
    {
        public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            if (reader.TokenType == JsonTokenType.String)
            {
                var s = reader.GetString();
                if (string.IsNullOrEmpty(s)) return null;
                return decimal.Parse(s, NumberStyles.Number, CultureInfo.InvariantCulture);
            }
            if (reader.TokenType == JsonTokenType.Number)
            {
                return reader.GetDecimal();
            }
            throw new JsonException(
                $"Expected JSON string, number, or null for decimal?, got {reader.TokenType}.");
        }

        public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
        {
            if (value is null) { writer.WriteNullValue(); return; }
            writer.WriteStringValue(value.Value.ToString(CultureInfo.InvariantCulture));
        }
    }
}
