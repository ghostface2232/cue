using System.Globalization;
using Cue.Domain;
using Cue.Parsing;

namespace Cue.Tests;

/// <summary>
/// Coverage of the opt-in "연중 주차" week-number rule: the recognized forms (W27 / 27주 / 27주차, with an
/// optional weekday, time, deadline marker, and locative josa), the guards that keep relative / anniversary
/// expressions out of it, the ISO year boundary, and the past-week roll-forward option.
/// </summary>
public sealed class WeekNumberRuleTests
{
    // Tuesday, 2026-06-23. ISO week of this day is W26 (W1 Monday is 2025-12-29), so W27 is the next, future
    // week and W10 is a past week — used by the roll-forward tests.
    private static readonly DateTimeOffset Now = new(2026, 6, 23, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 6, 23);
    private const string Tz = "UTC";

    private static IDateParser Parser(bool rollForward = false)
        => new KoreanDateParser(
            new IQuickAddRule[] { new WeekNumberRule() },
            new KoreanDateParserOptions { WeekNumberRollsForwardWhenPast = rollForward });

    private ParsedQuickAdd Parse(string input, bool rollForward = false)
        => Parser(rollForward).Parse(input, Now, Tz);

    private static DateOnly WhenDate(ScheduledWhen w) => DateOnly.FromDateTime(w.Date!.Value.ToLocal().DateTime);
    private static int WhenHour(ScheduledWhen w) => w.Date!.Value.ToLocal().Hour;
    private static int IsoWeek(DateOnly d) => ISOWeek.GetWeekOfYear(d.ToDateTime(TimeOnly.MinValue));
    private static int IsoYear(DateOnly d) => ISOWeek.GetYear(d.ToDateTime(TimeOnly.MinValue));

    [Theory]
    [InlineData("W27 회의", "회의")]
    [InlineData("w27 회의", "회의")]
    [InlineData("27주 회의", "회의")]
    [InlineData("27주차 회의", "회의")]
    [InlineData("27주에 회의", "회의")]
    [InlineData("W27에 회의", "회의")]
    [InlineData("W27까지 보고서", "보고서")]
    [InlineData("27주차까지 보고서", "보고서")]
    public void WeekForms_ResolveToThatWeeksMonday_WithCleanTitle(string input, string title)
    {
        var r = Parse(input);
        Assert.Equal(title, r.Title);
        Assert.Equal(WhenKind.OnDate, r.When.Kind);
        var date = WhenDate(r.When);
        Assert.Equal(27, IsoWeek(date));
        Assert.Equal(DayOfWeek.Monday, date.DayOfWeek);   // no weekday given → Monday
        Assert.Equal(new DateOnly(2026, 6, 29), date);    // W27 Monday of 2026
        Assert.False(r.WhenHasTime);
    }

    [Fact]
    public void WeekWithWeekday_LandsOnThatWeekday()
    {
        var r = Parse("W27 수요일 회의");
        Assert.Equal("회의", r.Title);
        var date = WhenDate(r.When);
        Assert.Equal(27, IsoWeek(date));
        Assert.Equal(DayOfWeek.Wednesday, date.DayOfWeek);
    }

    [Fact]
    public void WeekWithBareWeekday_LandsOnThatWeekday()
    {
        var r = Parse("27주차 금 회의");
        Assert.Equal("회의", r.Title);
        Assert.Equal(DayOfWeek.Friday, WhenDate(r.When).DayOfWeek);
        Assert.Equal(27, IsoWeek(WhenDate(r.When)));
    }

    [Fact]
    public void WeekWithTime_CarriesTheClock()
    {
        var r = Parse("27주 3시 회의");
        Assert.Equal("회의", r.Title);
        Assert.True(r.WhenHasTime);
        Assert.Equal(15, WhenHour(r.When));               // bare 3시 → afternoon
        Assert.Equal(DayOfWeek.Monday, WhenDate(r.When).DayOfWeek);
    }

    // The guards: relative / duration / anniversary forms must NOT be read as a week number.

    [Fact]
    public void RelativeWeeks_StayRelative_NotAWeekNumber()
    {
        // "27주 후" is 27 weeks from today (the built-in handles it), not ISO week 27.
        var r = Parse("27주 후 회의");
        Assert.Equal("회의", r.Title);
        Assert.Equal(WhenKind.OnDate, r.When.Kind);
        Assert.Equal(Today.AddDays(27 * 7), WhenDate(r.When));
    }

    [Theory]
    [InlineData("27주년 기념 행사")]   // anniversary
    [InlineData("27주째 모임")]        // ordinal duration
    public void AnniversaryAndOrdinal_StayInTitle(string input)
    {
        var r = Parse(input);
        Assert.Equal(WhenKind.Unscheduled, r.When.Kind);
        Assert.Contains("주", r.Title);
    }

    [Fact]
    public void OutOfRangeWeek_IsDeclined_AndStaysInTitle()
    {
        // No year has 54 ISO weeks, so this is never a valid week — left untouched.
        var r = Parse("W54 결산");
        Assert.Equal(WhenKind.Unscheduled, r.When.Kind);
        Assert.Contains("W54", r.Title);
    }

    [Fact]
    public void YearEndWeek_ResolvesViaIsoWeekNumberingYear()
    {
        // 2026 (Jan 1 is Thursday) is a 53-week year, so W53 is valid and its Monday is in late Dec 2026.
        var r = Parse("W53 마무리");
        Assert.Equal("마무리", r.Title);
        var date = WhenDate(r.When);
        Assert.Equal(53, IsoWeek(date));
        Assert.Equal(DayOfWeek.Monday, date.DayOfWeek);
    }

    [Fact]
    public void PastWeek_StaysInCurrentYear_ByDefault()
    {
        // W10 (March 2026) is already past relative to Now (W26); default keeps the current ISO year.
        var r = Parse("W10 회고", rollForward: false);
        var date = WhenDate(r.When);
        Assert.Equal(10, IsoWeek(date));
        Assert.Equal(2026, IsoYear(date));
        Assert.True(date < Today);
    }

    [Fact]
    public void PastWeek_RollsToNextYear_WhenOptionOn()
    {
        var r = Parse("W10 회고", rollForward: true);
        var date = WhenDate(r.When);
        Assert.Equal(10, IsoWeek(date));
        Assert.Equal(2027, IsoYear(date));
        Assert.True(date > Today);
    }

    [Fact]
    public void WithoutTheRule_WeekFormsStayInTitle()
    {
        // The default parser (no boost rule) must not recognize week numbers — the feature is opt-in.
        var plain = new KoreanDateParser().Parse("W27 회의", Now, Tz);
        Assert.Equal(WhenKind.Unscheduled, plain.When.Kind);
        Assert.Contains("W27", plain.Title);
    }
}
