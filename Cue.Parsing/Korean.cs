using System.Text.RegularExpressions;

namespace Cue.Parsing;

/// <summary>
/// Shared Korean grammar: weekday/numeral dictionaries, RRULE day codes, time resolution, and the
/// reusable regex fragments the built-in rules compose. Kept in one place so the rules stay small.
/// </summary>
internal static class Korean
{
    public static readonly IReadOnlyDictionary<char, DayOfWeek> Weekdays = new Dictionary<char, DayOfWeek>
    {
        ['월'] = DayOfWeek.Monday,
        ['화'] = DayOfWeek.Tuesday,
        ['수'] = DayOfWeek.Wednesday,
        ['목'] = DayOfWeek.Thursday,
        ['금'] = DayOfWeek.Friday,
        ['토'] = DayOfWeek.Saturday,
        ['일'] = DayOfWeek.Sunday,
    };

    /// <summary>RFC 5545 BYDAY token for a weekday.</summary>
    public static string ByDay(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "MO",
        DayOfWeek.Tuesday => "TU",
        DayOfWeek.Wednesday => "WE",
        DayOfWeek.Thursday => "TH",
        DayOfWeek.Friday => "FR",
        DayOfWeek.Saturday => "SA",
        _ => "SU",
    };

    private static readonly IReadOnlyDictionary<string, int> NativeSmallNumbers = new Dictionary<string, int>
    {
        ["한"] = 1, ["두"] = 2, ["세"] = 3, ["네"] = 4, ["다섯"] = 5,
        ["여섯"] = 6, ["일곱"] = 7, ["여덟"] = 8, ["아홉"] = 9, ["열"] = 10,
    };

    /// <summary>Parses an Arabic ("3") or native-Korean ("세") small number.</summary>
    public static int Number(string token)
        => int.TryParse(token, out var n) ? n : NativeSmallNumbers.GetValueOrDefault(token, 0);

    /// <summary>
    /// Resolves a matched time (with meridiem/half) to a 24h hour+minute, the evening flag, and
    /// whether a concrete clock time was actually given. Returns false for impossible times
    /// (e.g. "24시"), which the caller treats as "not a time" so it stays in the title.
    /// </summary>
    public static bool TryResolveTime(Match m, out int hour, out int minute, out bool evening, out bool hasTime)
    {
        hour = 0; minute = 0; evening = false; hasTime = false;

        var meridiem = First(m, "mer", "daypart");
        var isEveningWord = meridiem is "저녁" or "밤";
        evening = isEveningWord;

        if (!m.Groups["h"].Success)
        {
            // A bare day-part with no clock ("저녁", "점심때"). 점심 implies midday; evening words just
            // raise the flag; the rest only mark the part of day and carry no specific time.
            if (meridiem == "점심") { hour = 12; hasTime = true; return true; }
            if (isEveningWord) return true;               // evening flag, no concrete time
            return meridiem is "새벽" or "아침" or "오전" or "오후"; // consumed, no concrete time
        }

        var h = int.Parse(m.Groups["h"].Value);
        if (h is < 0 or > 23)
            return false;                                  // "24시" and friends are not clock times here

        if (m.Groups["half"].Success)
            minute = 30;
        else if (m.Groups["min"].Success)
            minute = int.Parse(m.Groups["min"].Value);
        if (minute is < 0 or > 59)
            return false;

        switch (meridiem)
        {
            case "오후" or "저녁" or "밤":
                if (h < 12) h += 12;
                break;
            case "오전" or "새벽" or "아침":
                if (h == 12) h = 0;
                break;
        }

        hour = h;
        hasTime = true;
        return true;
    }

    private static string? First(Match m, params string[] groups)
    {
        foreach (var g in groups)
            if (m.Groups[g].Success)
                return m.Groups[g].Value;
        return null;
    }

    // ---- Reusable pattern fragments ------------------------------------------

    /// <summary>Left boundary: the token must not be glued to a preceding Hangul syllable.</summary>
    public const string LeftEdge = @"(?<![가-힣])";

    /// <summary>Right boundary: nothing Hangul may follow (so "내일로", "오늘의집" don't match "내일"/"오늘").</summary>
    public const string RightEdge = @"(?![가-힣])";

    /// <summary>A date expression. Alternatives are ordered most-specific first.</summary>
    public const string Date =
        @"(?:" +
        @"(?<rel>오늘|내일모레|모레|글피|내일)" +
        @"|(?<weekend>이번\s*주말|주말)" +
        @"|다음\s*주\s*(?<nwwd>[월화수목금토일])요일" +
        @"|(?<nextweek>다음\s*주|담주)" +
        @"|(?<nextmonth>다음\s*달|담달)" +
        @"|(?<endmonth>이번\s*달\s*말일|이번\s*달\s*말)" +
        @"|(?<mon>\d{1,2})\s*월\s*(?<domd>\d{1,2})\s*일" +
        @"|(?<wd>[월화수목금토일])요일" +
        @"|(?<ndays>\d+)\s*일\s*(?:후|뒤|이따|있다가|지나서)" +
        @"|(?<nweeks>\d+)\s*주\s*(?:후|뒤)" +
        @"|(?<oneweek>일주일)\s*(?:후|뒤)" +
        @"|(?<nmonths>\d+)\s*(?:개월|달)\s*(?:후|뒤)" +
        @"|(?<domonly>\d{1,2})\s*일" +
        @")";

    /// <summary>A clock time, optionally with a meridiem/part-of-day prefix.</summary>
    public const string Time =
        @"(?:(?<mer>오전|오후|새벽|아침|저녁|밤|점심)\s*)?(?<h>\d{1,2})\s*시(?:\s*(?<min>\d{1,2})\s*분|\s*(?<half>반))?";

    /// <summary>A bare part-of-day with no clock ("저녁", "점심때", "오전").</summary>
    public const string DayPart = @"(?<daypart>새벽|아침|점심|오전|오후|저녁|밤)(?:에|때)?";

    /// <summary>The largest valid day for a month (leap-permissive for February).</summary>
    public static int MaxDayOfMonth(int month) => month switch
    {
        2 => 29,
        4 or 6 or 9 or 11 => 30,
        _ => 31,
    };

    /// <summary>
    /// Resolves the matched date alternative in <paramref name="m"/> to an absolute day. Returns
    /// false when a numeric component is out of range (e.g. "99일", "13월 40일") or the arithmetic
    /// overflows — the caller then declines the match, leaving the text in the title rather than
    /// inventing a clamped date.
    /// </summary>
    public static bool TryResolveDate(Match m, ParseContext ctx, out DateOnly date)
    {
        date = ctx.Today;
        try
        {
            if (m.Groups["rel"].Success)
            {
                date = ctx.Today.AddDays(m.Groups["rel"].Value switch
                {
                    "오늘" => 0,
                    "내일" => 1,
                    "모레" or "내일모레" => 2,
                    "글피" => 3,
                    _ => 0,
                });
                return true;
            }
            if (m.Groups["weekend"].Success) { date = ctx.UpcomingWeekday(DayOfWeek.Saturday); return true; }
            if (m.Groups["nwwd"].Success) { date = ctx.NextWeekWeekday(Weekdays[m.Groups["nwwd"].Value[0]]); return true; }
            if (m.Groups["nextweek"].Success) { date = ctx.Today.AddDays(7); return true; }
            if (m.Groups["nextmonth"].Success) { date = ctx.NextMonthSameDay(); return true; }
            if (m.Groups["endmonth"].Success) { date = ctx.EndOfThisMonth(); return true; }
            if (m.Groups["mon"].Success)
            {
                if (!int.TryParse(m.Groups["mon"].Value, out var month) || !int.TryParse(m.Groups["domd"].Value, out var day))
                    return false;
                if (month is < 1 or > 12 || day < 1 || day > MaxDayOfMonth(month))
                    return false;
                date = ctx.MonthDay(month, day);
                return true;
            }
            if (m.Groups["wd"].Success) { date = ctx.UpcomingWeekday(Weekdays[m.Groups["wd"].Value[0]]); return true; }
            if (m.Groups["ndays"].Success) { date = ctx.Today.AddDays(ParsePositive(m.Groups["ndays"].Value)); return true; }
            if (m.Groups["nweeks"].Success) { date = ctx.Today.AddDays(7 * ParsePositive(m.Groups["nweeks"].Value)); return true; }
            if (m.Groups["oneweek"].Success) { date = ctx.Today.AddDays(7); return true; }
            if (m.Groups["nmonths"].Success) { date = ctx.AddMonths(ParsePositive(m.Groups["nmonths"].Value)); return true; }
            if (m.Groups["domonly"].Success)
            {
                if (!int.TryParse(m.Groups["domonly"].Value, out var dom) || dom is < 1 or > 31)
                    return false;
                date = ctx.UpcomingDayOfMonth(dom);
                return true;
            }
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            // Absurd offsets ("99999999일 후") overflow DateOnly — treat as not-a-date.
            return false;
        }
    }

    private static int ParsePositive(string s) => int.TryParse(s, out var n) ? n : throw new ArgumentOutOfRangeException(nameof(s));
}
