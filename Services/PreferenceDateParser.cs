using Cue.Parsing;

namespace Cue.Services;

public sealed class PreferenceDateParser : IDateParser
{
    private readonly AppPreferences _preferences;

    public PreferenceDateParser(AppPreferences preferences)
    {
        _preferences = preferences;
    }

    public ParsedQuickAdd Parse(string input, DateTimeOffset now, string timeZoneId)
    {
        var customDates = _preferences.CustomDateMeanings
            .ToDictionary(meaning => meaning.Name, meaning => meaning.DayOfMonth, StringComparer.Ordinal);
        var rules = customDates.Count == 0
            ? Array.Empty<IQuickAddRule>()
            : [new CustomDayOfMonthRule(customDates)];
        var options = new KoreanDateParserOptions
        {
            AutoAfternoonForBareOneToSix = _preferences.AutoAfternoonForBareOneToSix,
        };
        return new KoreanDateParser(rules, options).Parse(input, now, timeZoneId);
    }
}
