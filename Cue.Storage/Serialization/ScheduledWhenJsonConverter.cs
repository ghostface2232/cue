using System.Text.Json;
using System.Text.Json.Serialization;
using Cue.Domain;

namespace Cue.Storage.Serialization;

/// <summary>
/// Serializes <see cref="ScheduledWhen"/> so the variant is explicit. Unscheduled and SomeDay are
/// just <c>{ "kind": "Unscheduled" }</c> / <c>{ "kind": "SomeDay" }</c>; only OnDate carries a date:
/// <c>{ "kind": "OnDate", "date": {…} }</c>. A legacy <c>isEvening</c> field is ignored on read.
/// Reading goes back through the domain factories, so an invalid combination can never be materialized.
/// </summary>
public sealed class ScheduledWhenJsonConverter : JsonConverter<ScheduledWhen>
{
    public override ScheduledWhen Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected an object for ScheduledWhen.");

        WhenKind? kind = null;
        ZonedDateTime? date = null;

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
                default:
                    reader.Skip();
                    break;
            }
        }

        return kind switch
        {
            WhenKind.Unscheduled => ScheduledWhen.Unscheduled,
            WhenKind.SomeDay => ScheduledWhen.SomeDay,
            WhenKind.OnDate => ScheduledWhen.On(
                date ?? throw new JsonException("An OnDate ScheduledWhen requires a 'date'.")),
            _ => throw new JsonException("Unknown or missing ScheduledWhen 'kind'."),
        };
    }

    public override void Write(Utf8JsonWriter writer, ScheduledWhen value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", value.Kind.ToString());
        if (value.Kind == WhenKind.OnDate)
        {
            writer.WritePropertyName("date");
            JsonSerializer.Serialize(writer, value.Date!.Value, options);
        }
        writer.WriteEndObject();
    }
}
