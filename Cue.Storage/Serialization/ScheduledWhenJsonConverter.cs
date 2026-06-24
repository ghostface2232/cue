using System.Text.Json;
using System.Text.Json.Serialization;
using Cue.Domain;

namespace Cue.Storage.Serialization;

/// <summary>
/// Serializes <see cref="ScheduledWhen"/> so the variant is explicit. Unscheduled is just
/// <c>{ "kind": "Unscheduled" }</c>; OnDate carries a date and its 종일 flag:
/// <c>{ "kind": "OnDate", "date": {…}, "isAllDay": false }</c>. Reading goes back through the domain
/// factories, so an invalid combination can never be materialized.
/// </summary>
public sealed class ScheduledWhenJsonConverter : JsonConverter<ScheduledWhen>
{
    // Legacy records (written before <c>isAllDay</c> existed) encoded an all-day date by pinning it to
    // the end of the day, 23:59 local. When the flag is absent we recover the intent from that marker so
    // old data keeps showing as 종일.
    private static readonly TimeOnly LegacyAllDayMarker = new(23, 59);

    public override ScheduledWhen Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected an object for ScheduledWhen.");

        WhenKind? kind = null;
        ZonedDateTime? date = null;
        bool? isAllDay = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException();

            var name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case "kind":
                    kind = Enum.Parse<WhenKind>(reader.GetString()!);
                    break;
                case "date":
                    date = reader.TokenType == JsonTokenType.Null
                        ? null
                        : JsonSerializer.Deserialize<ZonedDateTime>(ref reader, options);
                    break;
                case "isAllDay":
                    isAllDay = reader.TokenType == JsonTokenType.Null ? null : reader.GetBoolean();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return kind switch
        {
            WhenKind.Unscheduled => ScheduledWhen.Unscheduled,
            WhenKind.OnDate => BuildOnDate(date, isAllDay),
            _ => throw new JsonException("Unknown or missing ScheduledWhen 'kind'."),
        };
    }

    private static ScheduledWhen BuildOnDate(ZonedDateTime? date, bool? isAllDay)
    {
        var value = date ?? throw new JsonException("An OnDate ScheduledWhen requires a 'date'.");
        // The explicit flag wins; only legacy records (no flag) fall back to the old 23:59 marker.
        var allDay = isAllDay ?? IsLegacyAllDayMarker(value);
        return allDay ? ScheduledWhen.AllDay(value) : ScheduledWhen.On(value);
    }

    private static bool IsLegacyAllDayMarker(ZonedDateTime date)
        => TimeOnly.FromDateTime(date.ToLocal().DateTime) == LegacyAllDayMarker;

    public override void Write(Utf8JsonWriter writer, ScheduledWhen value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", value.Kind.ToString());
        if (value.Kind == WhenKind.OnDate)
        {
            writer.WritePropertyName("date");
            JsonSerializer.Serialize(writer, value.Date!.Value, options);
            writer.WriteBoolean("isAllDay", value.IsAllDay);
        }
        writer.WriteEndObject();
    }
}
