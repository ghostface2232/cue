using System.Text.Json;
using System.Text.Json.Serialization;
using Cue.Domain;

namespace Cue.Storage.Serialization;

/// <summary>
/// Serializes <see cref="RecurrenceRule"/> as <c>{ "rule": "FREQ=…", "anchor": {…} }</c> and rebuilds
/// it through the validating constructor, preserving the RRULE string verbatim and the zoned anchor.
/// A dedicated converter is needed because the rule's properties are constructor-only (get-only).
/// </summary>
public sealed class RecurrenceRuleJsonConverter : JsonConverter<RecurrenceRule>
{
    public override RecurrenceRule Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected an object for RecurrenceRule.");

        string? rule = null;
        ZonedDateTime? anchor = null;

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
                case "rule": rule = reader.GetString(); break;
                case "anchor": anchor = JsonSerializer.Deserialize<ZonedDateTime>(ref reader, options); break;
                default: reader.Skip(); break;
            }
        }

        if (rule is null || anchor is null)
            throw new JsonException("RecurrenceRule requires both 'rule' and 'anchor'.");

        return new RecurrenceRule(rule, anchor.Value);
    }

    public override void Write(Utf8JsonWriter writer, RecurrenceRule value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("rule", value.Rule);
        writer.WritePropertyName("anchor");
        JsonSerializer.Serialize(writer, value.Anchor, options);
        writer.WriteEndObject();
    }
}
