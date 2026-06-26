using System.Text.Json;
using System.Text.Json.Serialization;
using Cue.Domain;

namespace Cue.Storage.Serialization;

/// <summary>
/// A custom JSON converter for <see cref="OccurrenceStatus"/> that maps legacy "Skipped" (or integer values of 1 or 2)
/// to <see cref="OccurrenceStatus.Missed"/>, consolidating the "not performed" state.
/// </summary>
public sealed class OccurrenceStatusJsonConverter : JsonConverter<OccurrenceStatus>
{
    public override OccurrenceStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (string.Equals(value, "Completed", StringComparison.OrdinalIgnoreCase))
                return OccurrenceStatus.Completed;
            if (string.Equals(value, "Missed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "Skipped", StringComparison.OrdinalIgnoreCase))
                return OccurrenceStatus.Missed;

            throw new JsonException($"Unknown OccurrenceStatus value: {value}");
        }
        else if (reader.TokenType == JsonTokenType.Number)
        {
            var value = reader.GetInt32();
            return value switch
            {
                0 => OccurrenceStatus.Completed,
                1 => OccurrenceStatus.Missed, // legacy Skipped (1) is now Missed
                2 => OccurrenceStatus.Missed, // legacy Missed (2) remains Missed
                _ => throw new JsonException($"Unknown OccurrenceStatus integer value: {value}")
            };
        }

        throw new JsonException("Expected string or number for OccurrenceStatus.");
    }

    public override void Write(Utf8JsonWriter writer, OccurrenceStatus value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
