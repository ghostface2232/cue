using Cue.Parsing;

namespace Cue.Services;

/// <summary>
/// The app's <see cref="IDateParser"/>: a <see cref="KoreanDateParser"/> configured from the user's
/// preferences (custom date meanings + the bare-hour afternoon rule). Registered as a singleton, so the
/// underlying parser — whose construction builds the Microsoft.Recognizers DateTime model, a heavy step
/// (~14ms incl. its lazy first-parse warmup) — is cached and reused across calls, and rebuilt only when
/// the preference inputs that define it actually change. This matters because the inline quick-add
/// highlight re-parses on every keystroke; reconstructing the parser per call put that build on the UI
/// thread for each key, which is what made the quick-add box feel sluggish.
/// </summary>
public sealed class PreferenceDateParser : IDateParser
{
    private readonly AppPreferences _preferences;
    private readonly object _gate = new();

    private KoreanDateParser? _cached;
    private string? _cachedSignature;

    public PreferenceDateParser(AppPreferences preferences)
    {
        _preferences = preferences;
    }

    public ParsedQuickAdd Parse(string input, DateTimeOffset now, string timeZoneId)
        => GetParser().Parse(input, now, timeZoneId);

    public ParsedQuickAdd Parse(string input, DateTimeOffset now, string timeZoneId, IReadOnlyList<TextSpan> suppressedSpans)
        => GetParser().Parse(input, now, timeZoneId, suppressedSpans);

    /// <summary>Returns the cached parser, rebuilding it only when the preference signature changed.</summary>
    private KoreanDateParser GetParser()
    {
        var signature = BuildSignature();
        lock (_gate)
        {
            if (_cached is null || _cachedSignature != signature)
            {
                _cached = BuildParser();
                _cachedSignature = signature;
            }
            return _cached;
        }
    }

    private KoreanDateParser BuildParser()
    {
        var customDates = _preferences.CustomDateMeanings
            .ToDictionary(meaning => meaning.Name, meaning => meaning.DayOfMonth, StringComparer.Ordinal);

        // Boost rules run before the built-ins. The week-number rule is added only when the "연중 주차"
        // preference is on, so a user who doesn't use it never gets "27주"-style text parsed as a date.
        var rules = new List<IQuickAddRule>();
        if (_preferences.ShowWeekNumber)
            rules.Add(new WeekNumberRule());
        if (customDates.Count > 0)
            rules.Add(new CustomDayOfMonthRule(customDates));

        var options = new KoreanDateParserOptions
        {
            AutoAfternoonForBareOneToSix = _preferences.AutoAfternoonForBareOneToSix,
            WeekNumberRollsForwardWhenPast = _preferences.WeekNumberPastRollsToNextYear,
        };
        return new KoreanDateParser(rules, options);
    }

    /// <summary>A compact key for the preference inputs that determine the parser. When this is unchanged
    /// the cached parser is reused verbatim, so the recognizer is built at most once per distinct config.
    /// (<see cref="CustomDateMeanings"/> arrives in a stable, name-sorted order from the preference store.)</summary>
    private string BuildSignature()
        => ParserConfigKey.Build(
            _preferences.AutoAfternoonForBareOneToSix,
            _preferences.ShowWeekNumber,
            _preferences.WeekNumberPastRollsToNextYear,
            _preferences.CustomDateMeanings.Select(meaning => (meaning.Name, meaning.DayOfMonth)));
}
