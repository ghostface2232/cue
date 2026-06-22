using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Cue.Storage.Serialization;

/// <summary>
/// Builds the <see cref="JsonSerializerOptions"/> the store uses so domain records round-trip
/// exactly.
/// </summary>
public static class StoreSerialization
{
    /// <summary>Creates a fresh options instance configured for the domain model.</summary>
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // Write non-ASCII (Korean titles/notes) as real characters, not \uXXXX escapes, so the
            // per-record files are genuinely human-readable. Safe here: these are local files, not
            // HTML-embedded output.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            // Drop computed/derived properties (IsCompleted, IsDeleted, HasDate, …) without the
            // domain ever knowing about serialization: they have no setter, so they are removed
            // from the contract here. Real fields (IsArchived, etc.) have a setter and stay.
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers = { DropReadOnlyComputedProperties },
            },
        };

        // Enums as strings (Priority -> "P1", ProjectView -> "List").
        options.Converters.Add(new JsonStringEnumConverter());
        // Value types that construct through factories/validating ctors need explicit converters.
        options.Converters.Add(new ZonedDateTimeJsonConverter());
        options.Converters.Add(new ScheduledWhenJsonConverter());
        options.Converters.Add(new RecurrenceRuleJsonConverter());

        return options;
    }

    /// <summary>
    /// Removes get-only properties (no set/init accessor) from the serialized contract. On the
    /// record entities those are always derived values; the factory/ctor-constructed value types
    /// (ZonedDateTime, ScheduledWhen, RecurrenceRule) are handled by their own converters, so this
    /// never strips a real data field.
    /// </summary>
    private static void DropReadOnlyComputedProperties(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
            return;

        for (var i = typeInfo.Properties.Count - 1; i >= 0; i--)
        {
            if (typeInfo.Properties[i].Set is null)
                typeInfo.Properties.RemoveAt(i);
        }
    }
}
