using Cue.Domain;
using Cue.Parsing;

namespace Cue.Tests;

/// <summary>
/// Step 4 of the inline-highlight plan: reverting (suppressing) a recognized span. A suppressed span is
/// excluded from recognition — no token, no scheduling — yet stays in the final title (recognition
/// exclusion ≠ title removal). The parser stays pure and never throws.
/// </summary>
public sealed class QuickAddSuppressionTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 23, 0, 0, 0, TimeSpan.Zero);
    private const string Tz = "UTC";

    private readonly IDateParser _parser = new KoreanDateParser();

    private static TextSpan[] Spans(params (int Start, int Length)[] spans)
        => spans.Select(s => new TextSpan(s.Start, s.Length)).ToArray();

    [Fact]
    public void Suppressing_a_recognized_date_drops_its_token_and_scheduling_but_keeps_the_word_in_the_title()
    {
        const string input = "내일 장보기";

        var normal = _parser.Parse(input, Now, Tz);
        Assert.True(normal.WhenAssigned);
        Assert.Contains(normal.Tokens, t => t.Text == "내일");
        Assert.Equal("장보기", normal.Title);

        var reverted = _parser.Parse(input, Now, Tz, Spans((0, 2))); // "내일"
        Assert.False(reverted.WhenAssigned);
        Assert.Empty(reverted.Tokens);
        Assert.Equal(WhenKind.Unscheduled, reverted.When.Kind);
        Assert.Equal("내일 장보기", reverted.Title); // the reverted word stays in the title
    }

    [Fact]
    public void Suppressing_the_winning_date_flips_recognition_to_the_next_one()
    {
        // Two dates, one When slot: the first claims it (write-once), so only "오늘" is a token normally.
        const string input = "오늘 내일 메모";

        var normal = _parser.Parse(input, Now, Tz);
        Assert.Contains(normal.Tokens, t => t.Text == "오늘");

        var reverted = _parser.Parse(input, Now, Tz, Spans((0, 2))); // revert "오늘"
        Assert.True(reverted.WhenAssigned);
        Assert.Contains(reverted.Tokens, t => t.Text == "내일");
        Assert.DoesNotContain(reverted.Tokens, t => t.Text == "오늘");
        Assert.Equal("오늘 메모", reverted.Title); // reverted "오늘" kept; recognized "내일" removed
    }

    [Fact]
    public void Suppressing_a_recurrence_phrase_cancels_the_recurrence_and_keeps_it_in_the_title()
    {
        const string input = "매주 금요일 운동";

        var normal = _parser.Parse(input, Now, Tz);
        Assert.NotNull(normal.Recurrence);

        var reverted = _parser.Parse(input, Now, Tz, Spans((0, 7))); // "매주 금요일 "
        Assert.Null(reverted.Recurrence);
        Assert.False(reverted.WhenAssigned);
        Assert.Empty(reverted.Tokens);
        Assert.Equal("매주 금요일 운동", reverted.Title);
    }

    [Fact]
    public void Out_of_range_and_overlapping_spans_never_throw()
    {
        const string input = "내일 장보기";

        // Wildly out-of-range / overlapping spans must degrade, not throw.
        var reverted = _parser.Parse(input, Now, Tz, Spans((-3, 1000), (1, 1), (2, 2)));
        Assert.False(reverted.WhenAssigned);
        Assert.Empty(reverted.Tokens);
        Assert.Equal("내일 장보기", reverted.Title);
    }

    [Theory]
    [InlineData("내일 장보기")]
    [InlineData("내일 오후 3시 회의 준비")]
    [InlineData("매주 금요일 운동")]
    [InlineData("그냥 평범한 제목")]
    public void Empty_suppression_matches_the_three_arg_overload(string input)
    {
        var baseline = _parser.Parse(input, Now, Tz);
        var withEmpty = _parser.Parse(input, Now, Tz, Array.Empty<TextSpan>());

        Assert.Equal(baseline.Title, withEmpty.Title);
        Assert.Equal(baseline.WhenAssigned, withEmpty.WhenAssigned);
        // RecurrenceRule is a reference type without value equality — compare by its value projection.
        Assert.Equal(baseline.Recurrence?.Rule, withEmpty.Recurrence?.Rule);
        Assert.Equal(baseline.Recurrence?.Anchor, withEmpty.Recurrence?.Anchor);
        Assert.Equal(
            baseline.Tokens.Select(t => (t.Kind, t.Start, t.Length, t.Text)),
            withEmpty.Tokens.Select(t => (t.Kind, t.Start, t.Length, t.Text)));
    }
}
