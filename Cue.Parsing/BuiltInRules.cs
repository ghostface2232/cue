using System.Text.RegularExpressions;
using Cue.Domain;

namespace Cue.Parsing;

// The built-in Korean recognition rules. Each is small: a pattern plus an Extract that resolves the
// match through the shared Korean grammar and writes one slot. The parser runs them in the order
// listed in KoreanDateParser.DefaultRules.

file static class Opt
{
    public const RegexOptions Flags = RegexOptions.CultureInvariant | RegexOptions.Compiled;
}

/// <summary>언젠가 / 나중에 / 시간 날 때 / 여유 생기면 → <see cref="ScheduledWhen.SomeDay"/>.</summary>
public sealed class SomedayRule : IQuickAddRule
{
    public Regex Pattern { get; } = new(
        Korean.LeftEdge +
        @"(?:언젠가는|언젠가|나중에|시간\s*날\s*때|시간\s*나면|시간\s*있을\s*때|여유\s*생기면|여유\s*되면|여유\s*있을\s*때)" +
        Korean.RightEdge, Opt.Flags);

    public bool Extract(Match match, ParseContext context, QuickAddResult result)
        => result.TrySetWhen(ScheduledWhen.SomeDay);
}

/// <summary>한/두/세 시간 후, N시간 후 → a When at the clock-relative moment.</summary>
public sealed class RelativeHourRule : IQuickAddRule
{
    public Regex Pattern { get; } = new(
        Korean.LeftEdge +
        @"(?<n>\d+|한|두|세|네|다섯|여섯|일곱|여덟|아홉|열)\s*시간\s*(?:후|뒤|있다가|이따)(?:에)?" +
        Korean.RightEdge, Opt.Flags);

    public bool Extract(Match match, ParseContext context, QuickAddResult result)
    {
        var n = Korean.Number(match.Groups["n"].Value);
        if (n <= 0)
            return false;
        var target = context.LocalNow.AddHours(n);
        var date = DateOnly.FromDateTime(target.DateTime);
        return result.TrySetWhen(ScheduledWhen.On(context.Zoned(date, target.Hour, target.Minute)));
    }
}

/// <summary>매일 / 매주(요일) / 매월(N일) / 매년 / 격주 / 평일 / N분마다 → a <see cref="RecurrenceRule"/>.</summary>
public sealed class RecurrenceQuickAddRule : IQuickAddRule
{
    public Regex Pattern { get; } = new(
        Korean.LeftEdge +
        @"(?:" +
        @"(?<weekly_wd>매주|매\s*주)\s*(?<rwd>[월화수목금토일])요일" +
        @"|(?<biweekly>격주|이주마다|2주마다)\s*(?:(?<bwd>[월화수목금토일])요일)?" +
        @"|(?<weekdays>평일)" +
        @"|(?<daily>매일|매\s*일)" +
        @"|(?<weekly>매주|매\s*주)" +
        @"|(?<monthly_dom>매월|매달)\s*(?<mdom>\d{1,2})\s*일" +
        @"|(?<monthly>매월|매달)" +
        @"|(?<yearly>매년|매해)" +
        @"|(?<minutely>\d+)\s*분\s*마다" +
        @"|(?<hourly>\d+)\s*시간\s*마다" +
        @")" +
        @"(?:\s*(?:" + Korean.Time + "|" + Korean.DayPart + "))?", Opt.Flags);

    public bool Extract(Match match, ParseContext context, QuickAddResult result)
    {
        // A recurrence repeats at a fixed wall-clock time, so the "morning already passed" bump that
        // disambiguates one-off bare hours doesn't apply — meridiemGiven is intentionally ignored here.
        Korean.TryResolveTime(match, out var h, out var min, out _, out var hasTime, out _);

        string rule;
        ZonedDateTime anchor;

        if (match.Groups["weekly_wd"].Success)
        {
            var dow = Korean.Weekdays[match.Groups["rwd"].Value[0]];
            rule = $"FREQ=WEEKLY;BYDAY={Korean.ByDay(dow)}";
            anchor = AnchorOn(context, context.UpcomingWeekday(dow), hasTime, h, min);
        }
        else if (match.Groups["biweekly"].Success)
        {
            if (match.Groups["bwd"].Success)
            {
                var dow = Korean.Weekdays[match.Groups["bwd"].Value[0]];
                rule = $"FREQ=WEEKLY;INTERVAL=2;BYDAY={Korean.ByDay(dow)}";
                anchor = AnchorOn(context, context.UpcomingWeekday(dow), hasTime, h, min);
            }
            else
            {
                rule = "FREQ=WEEKLY;INTERVAL=2";
                anchor = AnchorOn(context, context.Today, hasTime, h, min);
            }
        }
        else if (match.Groups["weekdays"].Success)
        {
            rule = "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR";
            anchor = AnchorOn(context, context.Today, hasTime, h, min);
        }
        else if (match.Groups["daily"].Success)
        {
            rule = "FREQ=DAILY";
            anchor = AnchorOn(context, context.Today, hasTime, h, min);
        }
        else if (match.Groups["weekly"].Success)
        {
            rule = "FREQ=WEEKLY";
            anchor = AnchorOn(context, context.Today, hasTime, h, min);
        }
        else if (match.Groups["monthly_dom"].Success)
        {
            // BYMONTHDAY must be 1..31 (RFC 5545); "매월 99일" is not a valid rule — leave it as text.
            if (!int.TryParse(match.Groups["mdom"].Value, out var dom) || dom is < 1 or > 31)
                return false;
            rule = $"FREQ=MONTHLY;BYMONTHDAY={dom}";
            anchor = AnchorOn(context, context.UpcomingDayOfMonth(dom), hasTime, h, min);
        }
        else if (match.Groups["monthly"].Success)
        {
            rule = "FREQ=MONTHLY";
            anchor = AnchorOn(context, context.Today, hasTime, h, min);
        }
        else if (match.Groups["yearly"].Success)
        {
            rule = "FREQ=YEARLY";
            anchor = AnchorOn(context, context.Today, hasTime, h, min);
        }
        else if (match.Groups["minutely"].Success)
        {
            // INTERVAL must be a positive integer (RFC 5545); "0분마다" is not a valid rule.
            if (!int.TryParse(match.Groups["minutely"].Value, out var interval) || interval < 1)
                return false;
            rule = $"FREQ=MINUTELY;INTERVAL={interval}";
            anchor = context.ZonedNow;
        }
        else if (match.Groups["hourly"].Success)
        {
            if (!int.TryParse(match.Groups["hourly"].Value, out var interval) || interval < 1)
                return false;
            rule = $"FREQ=HOURLY;INTERVAL={interval}";
            anchor = context.ZonedNow;
        }
        else
        {
            return false;
        }

        return result.TrySetRecurrence(new RecurrenceRule(rule, anchor));
    }

    private static ZonedDateTime AnchorOn(ParseContext ctx, DateOnly date, bool hasTime, int h, int m)
        => ctx.Zoned(date, hasTime ? h : 0, hasTime ? m : 0);
}

/// <summary>…까지 / …마감 / N일 안에 → a <see cref="QuickAddResult.Deadline"/>.</summary>
public sealed class DeadlineRule : IQuickAddRule
{
    public Regex Pattern { get; } = new(
        Korean.LeftEdge +
        @"(?:" +
        @"(?<within>\d+)\s*일\s*(?:안에|이내)" +
        "|" + Korean.Date + @"(?:\s*(?:" + Korean.Time + "|" + Korean.DayPart + @"))?\s*(?:까지|마감)" +
        @")" +
        Korean.RightEdge, Opt.Flags);

    public bool Extract(Match match, ParseContext context, QuickAddResult result)
    {
        DateOnly date;
        if (match.Groups["within"].Success)
        {
            if (!int.TryParse(match.Groups["within"].Value, out var n) || n < 0)
                return false;
            try { date = context.Today.AddDays(n); }
            catch (ArgumentOutOfRangeException) { return false; }
        }
        else if (!Korean.TryResolveDate(match, context, out date))
        {
            return false;
        }

        Korean.TryResolveTime(match, out var h, out var min, out _, out var hasTime, out var meridiemGiven);
        if (hasTime)
            h = context.DisambiguateBareHour(date, h, min, meridiemGiven);
        return result.TrySetDeadline(context.Zoned(date, hasTime ? h : 0, hasTime ? min : 0));
    }
}

/// <summary>A date (relative / weekday / absolute) with an optional time → an OnDate When.</summary>
public sealed class WhenDateRule : IQuickAddRule
{
    public Regex Pattern { get; } = new(
        Korean.LeftEdge +
        Korean.Date +
        @"(?:\s*(?:" + Korean.Time + "|" + Korean.DayPart + "))?" +
        @"(?:\s*(?:에|에는|부터|쯤|즈음|경))?" +
        @"(?!\s*(?:까지|마감|안에|이내))" +
        Korean.RightEdge, Opt.Flags);

    public bool Extract(Match match, ParseContext context, QuickAddResult result)
    {
        if (!Korean.TryResolveDate(match, context, out var date))
            return false; // out-of-range date ("99일", "13월 40일") — leave it in the title
        Korean.TryResolveTime(match, out var h, out var min, out var evening, out var hasTime, out var meridiemGiven);
        if (hasTime)
            h = context.DisambiguateBareHour(date, h, min, meridiemGiven);
        var when = hasTime
            ? ScheduledWhen.On(context.Zoned(date, h, min), evening)
            : ScheduledWhen.On(context.Zoned(date), evening);
        return result.TrySetWhen(when);
    }
}

/// <summary>A bare time or part-of-day with no date → today at that time (or this evening).</summary>
public sealed class TimeOfDayRule : IQuickAddRule
{
    public Regex Pattern { get; } = new(
        Korean.LeftEdge +
        @"(?:" + Korean.Time + "|" + Korean.DayPart + ")" +
        Korean.RightEdge, Opt.Flags);

    public bool Extract(Match match, ParseContext context, QuickAddResult result)
    {
        if (!Korean.TryResolveTime(match, out var h, out var min, out var evening, out var hasTime, out var meridiemGiven))
            return false;
        if (!hasTime && !evening)
            return false; // a stray part-of-day ("오전") alone schedules nothing
        if (hasTime)
            h = context.DisambiguateBareHour(context.Today, h, min, meridiemGiven);
        return result.TrySetWhen(ScheduledWhen.On(context.Zoned(context.Today, hasTime ? h : 0, hasTime ? min : 0), evening));
    }
}
