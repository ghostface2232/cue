using Cue.Domain;
using Cue.Parsing;

namespace Cue.Tests;

/// <summary>
/// The token correct-popover (step 5) replaces a token's text with a preset Korean phrase and lets the
/// parser re-recognize it. That only works if every offered preset actually re-parses to its intended
/// kind — a preset the parser doesn't understand would silently turn the token into plain title text.
/// These tests pin each menu preset so a regression in the grammar can't quietly break the popover.
/// </summary>
public sealed class QuickAddPresetTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 23, 1, 0, 0, TimeSpan.Zero); // a Tuesday
    private const string Tz = "UTC";
    private readonly IDateParser _parser = new KoreanDateParser();

    [Theory]
    [InlineData("오늘")]
    [InlineData("내일")]
    [InlineData("모레")]
    [InlineData("이번 주말")]
    [InlineData("다음 주")]
    public void Date_presets_reparse_to_a_date(string preset)
    {
        var parsed = _parser.Parse($"{preset} 메모", Now, Tz);
        Assert.True(parsed.WhenAssigned, $"'{preset}' should be recognized as a date");
        Assert.Equal(WhenKind.OnDate, parsed.When.Kind);
        Assert.Contains(parsed.Tokens, t => t.Kind is QuickAddTokenKind.Date);
    }

    [Theory]
    [InlineData("매일")]
    [InlineData("평일")]
    [InlineData("매주 금요일")]
    [InlineData("격주 금요일")]
    public void Recurrence_presets_reparse_to_a_recurrence(string preset)
    {
        var parsed = _parser.Parse($"{preset} 운동", Now, Tz);
        Assert.NotNull(parsed.Recurrence);
        Assert.Contains(parsed.Tokens, t => t.Kind is QuickAddTokenKind.Recurrence);
    }

    // The weekday-date popover offers adjacent weekdays (전/후) as bare "{요일}" — every weekday must parse.
    [Theory]
    [InlineData("월요일")]
    [InlineData("화요일")]
    [InlineData("수요일")]
    [InlineData("목요일")]
    [InlineData("금요일")]
    [InlineData("토요일")]
    [InlineData("일요일")]
    public void Weekday_neighbor_presets_reparse_to_a_date(string weekday)
    {
        var parsed = _parser.Parse($"{weekday} 메모", Now, Tz);
        Assert.True(parsed.WhenAssigned);
        Assert.Equal(WhenKind.OnDate, parsed.When.Kind);
        Assert.Contains(parsed.Tokens, t => t.Kind is QuickAddTokenKind.Date);
    }

    // The "다음 주 {요일}" / "다다음 주 {요일}" week shifts must consume cleanly — no "다음 주"/"다다음 주"
    // left in the title (the leak that ruled out "이번 주 {요일}", which replaces to a bare weekday instead).
    [Theory]
    [InlineData("다음 주 월요일")]
    [InlineData("다음 주 금요일")]
    [InlineData("다음 주 일요일")]
    [InlineData("다다음 주 월요일")]
    [InlineData("다다음 주 금요일")]
    [InlineData("다다음 주 일요일")]
    public void Week_shift_presets_consume_cleanly(string preset)
    {
        var parsed = _parser.Parse($"{preset} 메모", Now, Tz);
        Assert.True(parsed.WhenAssigned);
        Assert.Equal(WhenKind.OnDate, parsed.When.Kind);
        Assert.Equal("메모", parsed.Title); // the week prefix must not leak into the title
    }

    // "다다음 주 {요일}" lands two ISO weeks ahead (one more than "다음 주"). Now = Tue 2026-06-23.
    [Fact]
    public void WeekAfterNext_weekday_is_two_weeks_ahead()
    {
        var nextWeek = _parser.Parse("다음 주 금요일 메모", Now, Tz);
        var weekAfter = _parser.Parse("다다음 주 금요일 메모", Now, Tz);

        Assert.Equal(new DateOnly(2026, 7, 3), DateOnly.FromDateTime(nextWeek.When.Date!.Value.ToLocal().DateTime));
        Assert.Equal(new DateOnly(2026, 7, 10), DateOnly.FromDateTime(weekAfter.When.Date!.Value.ToLocal().DateTime));
    }

    // The time popover's ±1 hour / AM-PM flip emit explicit 오전/오후 times via ClockText; every form the
    // formatter can produce (incl. midnight 오전 12시 and noon 오후 12시, and a minute suffix) must re-parse.
    [Theory]
    [InlineData("오전 12시")]
    [InlineData("오전 11시")]
    [InlineData("오후 12시")]
    [InlineData("오후 1시")]
    [InlineData("오후 2시 30분")]
    public void Explicit_clock_strings_reparse_to_a_time(string preset)
    {
        var parsed = _parser.Parse($"내일 {preset} 회의", Now, Tz);
        Assert.True(parsed.WhenAssigned);
        Assert.True(parsed.WhenHasTime);
        Assert.Contains(parsed.Tokens, t => t.Kind is QuickAddTokenKind.Time);
    }
}
