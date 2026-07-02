using System.Text.Json;
using System.Text.Json.Serialization;
using Cue.Domain;

namespace Cue.Storage.Serialization;

/// <summary>
/// Serializes <see cref="ZonedDateTime"/> as its UTC instant plus the original time-zone id,
/// e.g. <c>{ "utc": "2026-07-01T09:00:00+00:00", "timeZoneId": "Asia/Seoul" }</c>. This is the
/// canonical shape for every user-chosen scheduled date.
/// </summary>
public sealed class ZonedDateTimeJsonConverter : JsonConverter<ZonedDateTime>
{
    public override ZonedDateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected an object for ZonedDateTime.");

        DateTimeOffset? utc = null;
        string? timeZoneId = null;

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
                case "utc": utc = reader.GetDateTimeOffset(); break;
                case "timeZoneId": timeZoneId = reader.GetString(); break;
                default: reader.Skip(); break;
            }
        }

        if (utc is null || timeZoneId is null)
            throw new JsonException("ZonedDateTime requires both 'utc' and 'timeZoneId'.");

        // An unresolvable zone id (a hand-edited file, or a record synced from a platform whose zone
        // this machine can't resolve) would otherwise surface as TimeZoneNotFoundException far from the
        // read — deep inside the startup index rebuild or a display projection — where nothing isolates
        // it, so one bad record could block every launch. Folding it into JsonException here lands such
        // a record in the store's existing corrupt-record isolation (skipped and logged) instead.
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new JsonException($"Unknown time zone id '{timeZoneId}'.", exception);
        }

        return new ZonedDateTime(utc.Value, timeZoneId);
    }

    public override void Write(Utf8JsonWriter writer, ZonedDateTime value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("utc", value.Utc);
        writer.WriteString("timeZoneId", value.TimeZoneId);
        writer.WriteEndObject();
    }
}
