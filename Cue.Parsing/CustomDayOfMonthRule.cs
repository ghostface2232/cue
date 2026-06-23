using System.Text.RegularExpressions;
using Cue.Domain;

namespace Cue.Parsing;

/// <summary>
/// User-defined semantic day names such as "월급날" that map to a recurring day of month.
/// </summary>
public sealed class CustomDayOfMonthRule : IQuickAddRule
{
    private readonly IReadOnlyDictionary<string, int> _daysByName;

    public CustomDayOfMonthRule(IReadOnlyDictionary<string, int> daysByName)
    {
        _daysByName = daysByName
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is >= 1 and <= 31)
            .ToDictionary(pair => pair.Key.Trim(), pair => pair.Value);

        var terms = _daysByName.Keys
            .OrderByDescending(term => term.Length)
            .Select(Regex.Escape)
            .ToArray();

        var alternatives = terms.Length == 0 ? "(?!)" : string.Join("|", terms);
        Pattern = new Regex(
            Korean.LeftEdge +
            $@"(?<custom>{alternatives})" +
            @"(?:\s*(?:" + Korean.Time + "|" + Korean.DayPart + @"))?" +
            @"(?:\s*(?<deadline>까지|마감))?" +
            Korean.RightEdge,
            RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    public Regex Pattern { get; }

    public bool Extract(Match match, ParseContext context, QuickAddResult result)
    {
        var name = match.Groups["custom"].Value.Trim();
        if (!_daysByName.TryGetValue(name, out var day))
            return false;

        var date = context.UpcomingDayOfMonth(day);
        Korean.TryResolveTime(match, out var hour, out var minute, out var hasTime, out var meridiemGiven);
        if (hasTime)
            hour = context.DisambiguateBareHour(date, hour, minute, meridiemGiven);

        var zoned = context.Zoned(date, hasTime ? hour : 0, hasTime ? minute : 0);
        return match.Groups["deadline"].Success
            ? result.TrySetDeadline(zoned)
            : result.TrySetWhen(ScheduledWhen.On(zoned));
    }
}
