using Cue.Parsing;
using Cue.ViewModels;

namespace Cue.Tests;

/// <summary>
/// The token popover's quick-correction alternatives use a replace-and-reparse model: the chosen phrase is
/// written over the token and the parser re-recognizes it. A phrase the parser resolves to something other
/// than intended silently corrupts the token, so these tests resolve the generated replacements against the
/// real parser — not just check they parse. The headline case is the ±1 hour time option, which must never
/// cross midnight (the replacement swaps only the time word, never the date, so a day-crossed option would
/// land a full day off).
/// </summary>
public sealed class QuickAddAlternativesTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 23, 1, 0, 0, TimeSpan.Zero); // a Tuesday
    private const string Tz = "UTC";
    private readonly IDateParser _parser = new KoreanDateParser();

    private static QuickAddToken TimeToken() => new(QuickAddTokenKind.Time, 0, 1, "시각");

    /// <summary>The local hour a generated clock phrase resolves to, anchored on tomorrow so the date is
    /// fixed and only the time varies — the same round-trip the popover performs on commit.</summary>
    private int ResolveHour(string clockPhrase)
    {
        var parsed = _parser.Parse($"내일 {clockPhrase} 회의", Now, Tz);
        Assert.True(parsed.WhenHasTime, $"'{clockPhrase}' should resolve a time");
        return parsed.When.Date!.Value.ToLocal().Hour;
    }

    // 오후 11시 (23:00): "1시간 늦게" would be 00:00 — the next calendar day — but the replacement leaves the
    // date word alone, so it must be omitted rather than mis-date the task. "1시간 일찍" stays on the day (22:00).
    [Fact]
    public void Time_AtElevenPM_OmitsTheLaterOption_AndEarlierStaysOnTheSameDay()
    {
        var alts = QuickAddAlternatives.For(TimeToken(), new QuickAddPreview("내일", "오후 11:00", 23, 0));

        Assert.DoesNotContain(alts, a => a.Label.StartsWith("1시간 늦게"));
        var earlier = Assert.Single(alts, a => a.Label.StartsWith("1시간 일찍"));
        Assert.Equal(22, ResolveHour(earlier.Replacement));
    }

    // 오전 12시 (00:00, midnight): "1시간 일찍" would be the previous day's 23:00 — omitted. "1시간 늦게" is 01:00.
    [Fact]
    public void Time_AtMidnight_OmitsTheEarlierOption_AndLaterStaysOnTheSameDay()
    {
        var alts = QuickAddAlternatives.For(TimeToken(), new QuickAddPreview("내일", "오전 12:00", 0, 0));

        Assert.DoesNotContain(alts, a => a.Label.StartsWith("1시간 일찍"));
        var later = Assert.Single(alts, a => a.Label.StartsWith("1시간 늦게"));
        Assert.Equal(1, ResolveHour(later.Replacement));
    }

    // A mid-day time offers both ±1 hour options, each landing exactly one hour away on the same day, and the
    // AM/PM flip (±12h) which by construction never crosses midnight.
    [Fact]
    public void Time_MidDay_OffersBothHourShiftsAndAFlip_AllOnTheSameDay()
    {
        var alts = QuickAddAlternatives.For(TimeToken(), new QuickAddPreview("내일", "오후 2:30", 14, 30));

        Assert.Equal(13, ResolveHour(Assert.Single(alts, a => a.Label.StartsWith("1시간 일찍")).Replacement));
        Assert.Equal(15, ResolveHour(Assert.Single(alts, a => a.Label.StartsWith("1시간 늦게")).Replacement));
        Assert.Equal(2, ResolveHour(Assert.Single(alts, a => a.Label.StartsWith("오전으로")).Replacement)); // 14:30 → 02:30
    }

    [Fact]
    public void Time_WithNoResolvedClock_YieldsNoAlternatives()
    {
        var alts = QuickAddAlternatives.For(TimeToken(), new QuickAddPreview(null, null, null, null));
        Assert.Empty(alts);
    }

    // A bare weekday token offers the adjacent calendar days (하루 전/뒤) in the same week plus the same
    // weekday in the two other weeks — and never re-suggests the current week's bare weekday (a no-op swap).
    [Fact]
    public void BareWeekday_OffersAdjacentDays_AndTheOtherTwoWeeks_NotTheCurrentOne()
    {
        var token = new QuickAddToken(QuickAddTokenKind.Date, 0, 3, "금요일");
        var alts = QuickAddAlternatives.For(token, new QuickAddPreview(null, null, null, null));

        Assert.Contains(alts, a => a.Label.StartsWith("하루 전") && a.Replacement == "목요일");
        Assert.Contains(alts, a => a.Label.StartsWith("하루 뒤") && a.Replacement == "토요일");
        Assert.Contains(alts, a => a.Replacement == "다음 주 금요일");
        Assert.Contains(alts, a => a.Replacement == "다다음 주 금요일");
        Assert.DoesNotContain(alts, a => a.Replacement == "금요일"); // the current week isn't re-offered
    }
}
