using Cue.Domain;
using Cue.Storage.Recurrence;

namespace Cue.Tests;

/// <summary>
/// Exercises the next-occurrence engine directly: representative RRULEs, time-zone preservation
/// across DST boundaries, and graceful handling of unusable rules.
/// </summary>
public sealed class RecurrenceCalculatorTests
{
    private static RecurrenceRule Rule(string rrule, DateTime anchorWall, string tz)
        => new(rrule, ZonedDateTime.FromLocal(anchorWall, tz));

    /// <summary>The next occurrence after the anchor itself, projected back to local wall time.</summary>
    private static DateTimeOffset NextLocalAfterAnchor(RecurrenceRule rule)
    {
        var next = RecurrenceCalculator.Next(rule, rule.Anchor.Utc);
        Assert.NotNull(next);
        return next!.Value.ToLocal();
    }

    [Fact]
    public void Daily_AdvancesByOneDay()
    {
        // Anchor Mon 2026-06-22 09:00 in Seoul.
        var rule = Rule("FREQ=DAILY", new DateTime(2026, 6, 22, 9, 0, 0), "Asia/Seoul");
        var next = RecurrenceCalculator.Next(rule, rule.Anchor.Utc);

        Assert.NotNull(next);
        Assert.Equal("Asia/Seoul", next!.Value.TimeZoneId);
        var local = next.Value.ToLocal();
        Assert.Equal(new DateTime(2026, 6, 23, 9, 0, 0), local.DateTime);
    }

    [Fact]
    public void Weekly_OnWeekday_LandsOnNextMatchingWeekday()
    {
        // Mondays. Anchor is itself a Monday (2026-06-22) → next is the following Monday.
        var rule = Rule("FREQ=WEEKLY;BYDAY=MO", new DateTime(2026, 6, 22, 9, 0, 0), "Asia/Seoul");
        var local = NextLocalAfterAnchor(rule);

        Assert.Equal(new DateTime(2026, 6, 29, 9, 0, 0), local.DateTime);
        Assert.Equal(DayOfWeek.Monday, local.DayOfWeek);
    }

    [Fact]
    public void Monthly_OnDayOfMonth_LandsOnSameDayNextMonth()
    {
        var rule = Rule("FREQ=MONTHLY;BYMONTHDAY=15", new DateTime(2026, 6, 15, 9, 0, 0), "Asia/Seoul");
        var local = NextLocalAfterAnchor(rule);

        Assert.Equal(new DateTime(2026, 7, 15, 9, 0, 0), local.DateTime);
    }

    [Fact]
    public void Biweekly_AdvancesByTwoWeeks()
    {
        var rule = Rule("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO", new DateTime(2026, 6, 22, 9, 0, 0), "Asia/Seoul");
        var local = NextLocalAfterAnchor(rule);

        Assert.Equal(new DateTime(2026, 7, 6, 9, 0, 0), local.DateTime);
    }

    [Fact]
    public void Weekly_AcrossSpringForwardDst_KeepsLocalWallClockTime()
    {
        // US DST 2026 begins Sun 2026-03-08. A weekly Monday rule anchored the Monday before
        // (EST, -05:00) must still fire at 09:00 *local* the Monday after (EDT, -04:00) — the
        // wall-clock time is preserved; only the UTC instant shifts by the DST hour.
        var rule = Rule("FREQ=WEEKLY;BYDAY=MO", new DateTime(2026, 3, 2, 9, 0, 0), "America/New_York");
        var local = NextLocalAfterAnchor(rule);

        Assert.Equal(new DateTime(2026, 3, 9, 9, 0, 0), local.DateTime);
        Assert.Equal(9, local.Hour); // would be 8 (an hour adrift) if computed in UTC alone
        Assert.Equal(TimeSpan.FromHours(-4), local.Offset); // EDT after the boundary
    }

    [Fact]
    public void Monthly_AcrossSpringForwardDst_KeepsLocalWallClockTime()
    {
        // Feb 16 (EST) → Mar 16 (EDT): same 09:00 local, different offset.
        var rule = Rule("FREQ=MONTHLY;BYMONTHDAY=16", new DateTime(2026, 2, 16, 9, 0, 0), "America/New_York");
        var local = NextLocalAfterAnchor(rule);

        Assert.Equal(new DateTime(2026, 3, 16, 9, 0, 0), local.DateTime);
        Assert.Equal(9, local.Hour);
        Assert.Equal(TimeSpan.FromHours(-4), local.Offset);
    }

    [Fact]
    public void NextStrictlyAfterAGivenInstant_SkipsThatInstant()
    {
        var rule = Rule("FREQ=DAILY", new DateTime(2026, 6, 22, 9, 0, 0), "Asia/Seoul");

        // Asking for the next occurrence after the 2026-06-23 occurrence returns 2026-06-24.
        var jun23 = RecurrenceCalculator.Next(rule, rule.Anchor.Utc)!.Value;
        var jun24 = RecurrenceCalculator.Next(rule, jun23.Utc);

        Assert.NotNull(jun24);
        Assert.Equal(new DateTime(2026, 6, 24, 9, 0, 0), jun24!.Value.ToLocal().DateTime);
    }

    [Fact]
    public void ExhaustedSeries_ReturnsNull()
    {
        // COUNT=1 means only the anchor occurrence exists; there is no "next".
        var rule = Rule("FREQ=DAILY;COUNT=1", new DateTime(2026, 6, 22, 9, 0, 0), "Asia/Seoul");
        Assert.Null(RecurrenceCalculator.Next(rule, rule.Anchor.Utc));
    }

    [Theory]
    [InlineData("this is not an rrule")]
    [InlineData("FREQ=NONSENSE")]
    public void UnrecognizedRule_ReturnsNull_DoesNotThrow(string rrule)
    {
        var rule = Rule(rrule, new DateTime(2026, 6, 22, 9, 0, 0), "Asia/Seoul");
        Assert.Null(RecurrenceCalculator.Next(rule, rule.Anchor.Utc));
    }
}
