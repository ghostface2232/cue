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

/// <summary>언젠가 / 나중에 / 다음에 / 담에 / 시간 나면 / 여유되면 / 기회 되면 → an explicit
/// <see cref="ScheduledWhen.Unscheduled"/> (the "언젠가" bucket; there is no separate Someday state).
/// The phrase is still recognized and stripped from the title; it just resolves to no date.
/// All multi-word markers use <c>\s*</c> so they match with or without the space ("여유되면" / "여유 되면").</summary>
public sealed class SomedayRule : IQuickAddRule
{
    public QuickAddTokenKind TokenKind => QuickAddTokenKind.Someday;

    public Regex Pattern { get; } = new(
        Korean.LeftEdge +
        @"(?:언젠가는|언젠가|나중에|다음에|담에" +
        @"|시간\s*날\s*때|시간\s*나면|시간\s*있을\s*때" +
        @"|여유\s*생기면|여유\s*되면|여유\s*있으면|여유\s*있을\s*때" +
        @"|기회\s*되면|기회\s*생기면|기회\s*있으면|기회\s*있을\s*때)" +
        Korean.RightEdge, Opt.Flags);

    public bool Extract(Match match, ParseContext context, QuickAddResult result)
        => result.TrySetWhen(ScheduledWhen.Unscheduled);
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
        return result.TrySetWhen(ScheduledWhen.On(context.Zoned(date, target.Hour, target.Minute)), hasTime: true);
    }
}

/// <summary>매일 / 매주(요일) / 매월(N일) / 매년 / 격주 / 평일 / N분마다 → a <see cref="RecurrenceRule"/>.</summary>
public sealed class RecurrenceQuickAddRule : IQuickAddRule
{
    private const string TimeWithParticle =
        @"(?:" + Korean.Time + "|" + Korean.DayPart + @")(?:\s*(?:에는|에도|엔|에|은|는|도|부터|쯤|즈음|경|(?<mada>마다)))?";

    public QuickAddTokenKind TokenKind => QuickAddTokenKind.Recurrence;

    public Regex Pattern { get; } = new(
        Korean.LeftEdge +
        // The recurrence expression is captured as `recur` so it can be located as one token, separate
        // from any trailing time (which carries its own `time` group).
        @"(?<recur>" +
        // A weekday, optionally led by 매주 and/or trailed by 마다. The 매주/마다 is what marks it
        // recurring; a bare weekday ("목욜", "금요일에") carries no such marker and is left for the
        // one-off WhenDateRule (the Extract returns false when neither is present).
        @"(?:(?<weekly_wd>매주|매\s*주)\s*)?(?<rwd>[월화수목금토일])(?:요일|욜)(?<wd_mada>마다)?" +
        @"|(?<biweekly>격주|이주마다|2주마다)\s*(?:(?<bwd>[월화수목금토일])(?:요일|욜)(?:마다)?)?" +
        @"|(?:(?:매주|매\s*주)\s*)?(?<weekdays>평일)(?:에는|에도|엔|에|은|는|도|마다)?" +
        @"|(?<daily>매일|매\s*일)" +
        @"|(?<weekly>매주|매\s*주)" +
        @"|(?<monthly_dom>매월|매달)\s*(?<mdom>\d{1,2})\s*일" +
        @"|(?<monthly>매월|매달)" +
        @"|(?<yearly>매년|매해|해마다)" +
        @"|(?<minutely_once>\d+)\s*분\s*에\s*한\s*번\s*씩" +
        @"|(?<minutely>\d+)\s*분\s*마다" +
        @"|(?<hourly>\d+)\s*시간\s*마다" +
        @")" +              // close (?<recur>…)
        @"(?:\s*" + TimeWithParticle + ")?", Opt.Flags);

    public bool Extract(Match match, ParseContext context, QuickAddResult result)
    {
        // A recurrence repeats at a fixed wall-clock time, so the "morning already passed" bump that
        // disambiguates one-off bare hours doesn't apply — meridiemGiven is intentionally ignored here.
        Korean.TryResolveTime(match, out var h, out var min, out var hasTime, out _);

        string rule;
        ZonedDateTime anchor;

        if (match.Groups["rwd"].Success)
        {
            // A weekday is recurring only when marked: a leading 매주, a 마다 glued to the weekday
            // ("목욜마다"), or a 마다 closing the trailing time ("금욜 저녁마다"). A bare weekday
            // ("목욜", "토욜 아침에") is a one-off — decline so WhenDateRule resolves it.
            if (!match.Groups["weekly_wd"].Success && !match.Groups["wd_mada"].Success && !match.Groups["mada"].Success)
                return false;
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
        else if (match.Groups["minutely_once"].Success)
        {
            if (!int.TryParse(match.Groups["minutely_once"].Value, out var interval) || interval < 1)
                return false;
            rule = $"FREQ=MINUTELY;INTERVAL={interval}";
            anchor = context.ZonedNow;
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

        // The anchor carries a meaningful time when the user typed one, or when the frequency is
        // sub-daily — a 분/시간 repeat anchors on the current instant and is inherently time-based, so it
        // must not be flattened to an all-day (종일) date by the quick-add path.
        var anchorHasTime = hasTime || ImpliesTimeOfDay(rule);
        return result.TrySetRecurrence(new RecurrenceRule(rule, anchor), anchorHasTime);
    }

    /// <summary>True for sub-daily frequencies, whose anchor (the current instant) always carries a
    /// meaningful time even when the user typed no clock time.</summary>
    private static bool ImpliesTimeOfDay(string rule)
        => rule.Contains("FREQ=SECONDLY", StringComparison.OrdinalIgnoreCase)
        || rule.Contains("FREQ=MINUTELY", StringComparison.OrdinalIgnoreCase)
        || rule.Contains("FREQ=HOURLY", StringComparison.OrdinalIgnoreCase);

    private static ZonedDateTime AnchorOn(ParseContext ctx, DateOnly date, bool hasTime, int h, int m)
        => ctx.Zoned(date, hasTime ? h : 0, hasTime ? m : 0);
}

/// <summary>…까지 / …마감 / N일 안에 → an OnDate <see cref="QuickAddResult.When"/>. A task has a single
/// date, so a "due" phrase resolves to the same When as a plain scheduled date ("금요일까지 보고서" →
/// title "보고서", When 금요일). The phrase is still recognized and stripped from the title.</summary>
public sealed class DeadlineRule : IQuickAddRule
{
    public Regex Pattern { get; } = new(
        Korean.LeftEdge +
        @"(?:" +
        @"(?<within>\d+)\s*일\s*(?:안에|이내)" +
        "|" + Korean.Date + @"(?:\s*(?:" + Korean.Time + "|" + Korean.DayPart + @"))?\s*(?:까지|마감)" +
        @"|(?:" + Korean.Time + "|" + Korean.DayPart + @")\s*(?:까지|마감)" +
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

        Korean.TryResolveTime(match, out var h, out var min, out var hasTime, out var meridiemGiven);
        if (hasTime)
            h = context.DisambiguateBareHour(date, h, min, meridiemGiven);
        var when = hasTime
            ? ScheduledWhen.On(context.Zoned(date, h, min))
            : ScheduledWhen.On(context.Zoned(date));
        return result.TrySetWhen(when, hasTime);
    }
}

/// <summary>A date (relative / weekday / absolute) with an optional time → an OnDate When.</summary>
public sealed class WhenDateRule : IQuickAddRule
{
    public Regex Pattern { get; } = new(
        Korean.LeftEdge +
        Korean.Date +
        @"(?:\s*(?:" + Korean.Time + "|" + Korean.DayPart + "))?" +
        @"(?:\s*(?:에는|에도|에|은|는|도|부터|쯤|즈음|경))?" +
        @"(?!\s*(?:까지|마감|안에|이내))" +
        Korean.RightEdge, Opt.Flags);

    public bool Extract(Match match, ParseContext context, QuickAddResult result)
    {
        if (!Korean.TryResolveDate(match, context, out var date))
            return false; // out-of-range date ("99일", "13월 40일") — leave it in the title
        Korean.TryResolveTime(match, out var h, out var min, out var hasTime, out var meridiemGiven);
        if (match.Groups["rel"].Success && match.Groups["rel"].Value == "이따" && hasTime && !meridiemGiven && h is >= 7 and <= 11)
            h += 12;
        if (hasTime)
            h = context.DisambiguateBareHour(date, h, min, meridiemGiven);
        var when = hasTime
            ? ScheduledWhen.On(context.Zoned(date, h, min))
            : ScheduledWhen.On(context.Zoned(date));
        return result.TrySetWhen(when, hasTime);
    }
}

/// <summary>A bare time or part-of-day with no date → today at that time, or tomorrow when that
/// moment has already passed (a date-less time earlier than "now" rolls to the next day).</summary>
public sealed class TimeOfDayRule : IQuickAddRule
{
    public QuickAddTokenKind TokenKind => QuickAddTokenKind.Time;

    public Regex Pattern { get; } = new(
        Korean.LeftEdge +
        @"(?:" + Korean.Time + "|" + Korean.DayPart + ")" +
        @"(?:\s*(?:에는|에도|엔|에|은|는|도|부터|쯤|즈음|경))?" +
        Korean.RightEdge, Opt.Flags);

    public bool Extract(Match match, ParseContext context, QuickAddResult result)
    {
        if (!Korean.TryResolveTime(match, out var h, out var min, out var hasTime, out var meridiemGiven))
            return false;
        if (!hasTime)
            return false; // nothing concrete to schedule
        h = context.DisambiguateBareHour(context.Today, h, min, meridiemGiven);
        var date = context.BareTimeDate(h, min);
        return result.TrySetWhen(ScheduledWhen.On(context.Zoned(date, h, min)), hasTime: true);
    }
}

/// <summary>점심 먹고 → the same representative time as the bare lunch day-part.</summary>
public sealed class MealAfterRule : IQuickAddRule
{
    public QuickAddTokenKind TokenKind => QuickAddTokenKind.Time;

    public Regex Pattern { get; } = new(
        Korean.LeftEdge +
        @"(?<meal>아침|점심|저녁)\s*(?:먹고|먹은\s*뒤|식사\s*후)" +
        Korean.RightEdge, Opt.Flags);

    public bool Extract(Match match, ParseContext context, QuickAddResult result)
    {
        var hour = match.Groups["meal"].Value switch
        {
            "아침" => 9,
            "점심" => 12,
            "저녁" => 18,
            _ => 0,
        };
        return hour > 0
            && result.TrySetWhen(ScheduledWhen.On(context.Zoned(context.BareTimeDate(hour, 0), hour, 0)), hasTime: true);
    }
}
