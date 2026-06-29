using System.Text.RegularExpressions;
using Cue.Domain;

namespace Cue.Parsing;

/// <summary>
/// Recognizes an ISO-8601 week-of-year reference and resolves it to a concrete date — the "연중 주차"
/// business shorthand. Recognized forms: <c>W27</c> / <c>w27</c>, <c>27주</c>, <c>27주차</c>, each optionally
/// followed by a weekday (<c>수</c>, <c>수요일</c>, <c>수욜</c>), a clock time, a deadline marker
/// (<c>까지</c>/<c>마감</c>/<c>안에</c>), and a locative josa (<c>에</c>/<c>엔</c>/<c>에는</c>/<c>은</c>/<c>는</c>) —
/// all consumed so they never land in the title. With no weekday it resolves to that week's Monday (ISO weeks
/// start Monday). The single-date model means a deadline marker resolves to the same <see cref="ScheduledWhen"/>
/// as a plain date.
/// <para>
/// This rule is injected (by the app) only when the user's "연중 주차" preference is on, and it runs before the
/// built-ins so it claims "27주차" before anything else. A trailing relative/duration/recurring/anniversary
/// suffix (<c>후</c>/<c>뒤</c>/<c>째</c>/<c>년</c>/<c>간</c>/<c>동안</c>/<c>마다</c>/<c>지나</c>) makes it decline, so
/// "27주 후" stays a relative "27 weeks later" and "27주년" stays an anniversary in the title.
/// </para>
/// </summary>
public sealed class WeekNumberRule : IQuickAddRule
{
    public WeekNumberRule()
    {
        Pattern = new Regex(
            // Not glued to a preceding letter, digit, or Hangul syllable (so "VW27" / "127주" don't match).
            @"(?<![A-Za-z0-9가-힣])" +
            // The W-form pins its digit run with a trailing non-digit boundary so it can neither backtrack a
            // 2-digit run down to 1 to satisfy the decline lookahead below ("W27 후" must stay relative, not
            // become W2) nor truncate a longer number ("W100" must not read as W10 + a stray "0"). The 주-form
            // needs no such guard — the literal "주" already anchors the right edge of its digits.
            @"(?:[Ww](?<wkw>\d{1,2})(?![0-9])|(?<wkk>\d{1,2})\s*주(?<cha>차)?)" +
            // Decline relative/duration/recurring/anniversary forms ("27주 후", "27주째", "27주년", "27주마다").
            @"(?!\s*(?:후|뒤|째|년|간|동안|마다|지나))" +
            // Optional weekday: the long form (요일/욜) always, or a bare single char only at a word boundary
            // (so "27주 수정" keeps "수정" as the title rather than reading "수" as Wednesday).
            @"(?:\s*(?<wd>[월화수목금토일])(?:요일|욜|(?![가-힣])))?" +
            // Optional clock time / part-of-day.
            @"(?:\s*(?:" + Korean.Time + "|" + Korean.DayPart + @"))?" +
            // Optional deadline marker — recognized and stripped, but resolves to the same single date.
            @"(?:\s*(?<deadline>까지|마감|안에))?" +
            // Optional locative josa.
            @"(?:(?<josa>에는|엔|에|은|는))?" +
            Korean.RightEdge,
            RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    public Regex Pattern { get; }

    public bool Extract(Match match, ParseContext context, QuickAddResult result)
    {
        var weekText = match.Groups["wkw"].Success ? match.Groups["wkw"].Value : match.Groups["wkk"].Value;
        if (!int.TryParse(weekText, out var week))
            return false;

        var weekday = match.Groups["wd"].Success
            ? Korean.Weekdays[match.Groups["wd"].Value[0]]
            : DayOfWeek.Monday;

        if (!context.TryWeekDate(week, weekday, out var date))
            return false;

        Korean.TryResolveTime(match, out var hour, out var minute, out var hasTime, out var meridiemGiven);
        if (hasTime)
            hour = context.DisambiguateBareHour(date, hour, minute, meridiemGiven);

        var zoned = context.Zoned(date, hasTime ? hour : 0, hasTime ? minute : 0);
        return result.TrySetWhen(ScheduledWhen.On(zoned), hasTime);
    }
}
