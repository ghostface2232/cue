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
        return new KoreanDateParser(rules, options).Parse(input, now, timeZoneId);
    }
}
