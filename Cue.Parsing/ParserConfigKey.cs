using System.Text;

namespace Cue.Parsing;

/// <summary>
/// Builds a collision-free cache key for the inputs that define a <see cref="KoreanDateParser"/> instance —
/// the parser feature toggles plus the user's custom date meanings. A caller (the app's preference-backed
/// parser) reuses one parser while this key is unchanged and rebuilds it when any parser input changes.
/// </summary>
/// <remarks>
/// The custom-date names are arbitrary user strings, so the key <b>length-prefixes</b> each name instead of
/// just joining on <c>=</c>/<c>;</c>: otherwise a name containing those delimiters could forge a key equal to
/// a different set — e.g. <c>{"a"→1, "b"→2}</c> and <c>{"a=1;b"→2}</c> both render as <c>a=1;b=2;</c>, so two
/// distinct configurations would share a cache entry and the wrong parser would be served.
/// </remarks>
public static class ParserConfigKey
{
    /// <summary>The cache key for the given parser configuration. Enumeration order of
    /// <paramref name="customDateMeanings"/> is significant, so the caller should pass them in a stable order.</summary>
    public static string Build(
        bool autoAfternoonForBareOneToSix,
        bool showWeekNumber,
        bool weekNumberRollsForwardWhenPast,
        IEnumerable<(string Name, int DayOfMonth)> customDateMeanings)
    {
        var sb = new StringBuilder();
        sb.Append(autoAfternoonForBareOneToSix ? '1' : '0')
            .Append(showWeekNumber ? '1' : '0')
            .Append(weekNumberRollsForwardWhenPast ? '1' : '0')
            .Append('|');
        foreach (var (name, day) in customDateMeanings)
        {
            var safeName = name ?? string.Empty;
            // {nameLength}:{name}={day}; — the length prefix makes the variable-length name unambiguous, so
            // delimiter characters inside it can't slide the field boundaries into a colliding key.
            sb.Append(safeName.Length).Append(':').Append(safeName).Append('=').Append(day).Append(';');
        }
        return sb.ToString();
    }
}
