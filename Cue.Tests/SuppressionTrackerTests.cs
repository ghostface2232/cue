using Cue.Parsing;
using Cue.ViewModels;

namespace Cue.Tests;

/// <summary>
/// Step 4.3: reprojecting reverted (suppressed) spans across a single contiguous text edit. Spans before
/// the edit shift, spans after it stay, and a span the edit touched is dropped (that word was edited).
/// </summary>
public sealed class SuppressionTrackerTests
{
    private static TextSpan[] Re(TextSpan[] spans, string oldText, string newText)
        => SuppressionTracker.Reproject(spans, oldText, newText).ToArray();

    private static TextSpan[] One(int start, int length) => new[] { new TextSpan(start, length) };

    [Fact]
    public void Insert_before_the_span_shifts_it_right()
    {
        // "금요일 회의" → insert "내일 " at the front. The reverted "금요일" [0,3] moves to [3,3].
        var moved = Re(One(0, 3), "금요일 회의", "내일 금요일 회의");
        Assert.Equal(new TextSpan(3, 3), Assert.Single(moved));
    }

    [Fact]
    public void Insert_after_the_span_leaves_it_unchanged()
    {
        // Reverted "금요일" [0,3]; append text after it — coordinates unchanged.
        var moved = Re(One(0, 3), "금요일 회의", "금요일 회의 자료");
        Assert.Equal(new TextSpan(0, 3), Assert.Single(moved));
    }

    [Fact]
    public void Delete_before_the_span_shifts_it_left()
    {
        // "내일 금요일" reverted "금요일" [3,3]; delete the leading "내일 " (4 chars) → [-? ] → [-1+? ]
        // removed 4 before start → start 3-4? No: removing "내일 " (indices 0..3, length 4 incl space)
        // wait "내일 " is 3 chars (내,일,space). Delete [0,3): "금요일" moves from [3,3] to [0,3].
        var moved = Re(One(3, 3), "내일 금요일", "금요일");
        Assert.Equal(new TextSpan(0, 3), Assert.Single(moved));
    }

    [Fact]
    public void Editing_inside_the_span_drops_it()
    {
        // "금요일 회의" reverted "금요일" [0,3]; user changes "금요일" → "금욜일" (edits inside). Dropped.
        var moved = Re(One(0, 3), "금요일 회의", "금욜일 회의");
        Assert.Empty(moved);
    }

    [Fact]
    public void Deleting_the_last_char_of_the_span_drops_it()
    {
        // Backspacing into the reverted word edits its range → revert no longer applies.
        var moved = Re(One(0, 3), "금요일 회의", "금요 회의");
        Assert.Empty(moved);
    }

    [Fact]
    public void Appending_right_after_the_span_keeps_it()
    {
        // Typing "마다" right after reverted "금요일" [0,3] (e.g. 금요일 → 금요일마다) does not edit the
        // span's own range, so the revert is preserved; the re-parse can then re-classify the larger phrase.
        var moved = Re(One(0, 3), "금요일", "금요일마다");
        Assert.Equal(new TextSpan(0, 3), Assert.Single(moved));
    }

    [Fact]
    public void Clearing_all_text_drops_every_span()
    {
        var moved = Re(new[] { new TextSpan(0, 3), new TextSpan(4, 2) }, "금요일 회의", "");
        Assert.Empty(moved);
    }

    [Fact]
    public void Multiple_spans_shift_independently_around_an_edit()
    {
        // "오늘 금요일 회의" with two reverts: "오늘" [0,2] and "금요일" [3,3]. Insert "꼭 " at the front
        // (2 chars: 꼭 + space) → both shift right by 2: [2,2] and [5,3].
        var moved = Re(new[] { new TextSpan(0, 2), new TextSpan(3, 3) }, "오늘 금요일 회의", "꼭 오늘 금요일 회의");
        Assert.Equal(new[] { new TextSpan(2, 2), new TextSpan(5, 3) }, moved);
    }

    [Fact]
    public void An_edit_before_one_span_and_after_another_moves_only_the_later_one()
    {
        // Spans "오늘" [0,2] and "회의" [7,2] in "오늘 금요일 회의"; delete the middle "금요일 " (indices
        // 3..7, 4 chars: 금,요,일,space). "오늘" is before the edit (unchanged), "회의" is after (shift -4).
        var moved = Re(new[] { new TextSpan(0, 2), new TextSpan(7, 2) }, "오늘 금요일 회의", "오늘 회의");
        Assert.Equal(new[] { new TextSpan(0, 2), new TextSpan(3, 2) }, moved);
    }

    [Fact]
    public void No_change_and_empty_inputs_are_returned_as_is()
    {
        var spans = One(0, 3);
        Assert.Same(spans, SuppressionTracker.Reproject(spans, "금요일 회의", "금요일 회의"));
        Assert.Empty(SuppressionTracker.Reproject(System.Array.Empty<TextSpan>(), "a", "b"));
    }
}
