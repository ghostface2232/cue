using Cue.Domain;

namespace Cue.Parsing;

/// <summary>What a recognized token represents — drives the click-to-correct popover later, and lets
/// the inline view treat date/time/recurrence as independently editable units. The inline accent is a
/// single colour regardless of kind; the kind is metadata, not a colour.</summary>
public enum QuickAddTokenKind
{
    /// <summary>A date expression (relative, weekday, or absolute): "내일", "금요일", "3월 15일", "다음 주 금요일".</summary>
    Date,
    /// <summary>A clock time or part-of-day: "3시", "오후 3시 반", "저녁".</summary>
    Time,
    /// <summary>A recurrence: "매주 금요일", "매일", "격주", "5분마다".</summary>
    Recurrence,
    /// <summary>An explicit unscheduled marker: "언젠가", "나중에".</summary>
    Someday,
}

/// <summary>
/// One recognized span the parser lifted out of the title, located in the <i>original</i> input so the
/// inline view can accent exactly those characters and the popover can offer alternatives or revert.
/// </summary>
/// <param name="Kind">What the span represents.</param>
/// <param name="Start">Char offset into the original input (the visible text passed to Parse).</param>
/// <param name="Length">Char length in the original input.</param>
/// <param name="Text">The original substring, kept verbatim for revert/display.</param>
public sealed record QuickAddToken(QuickAddTokenKind Kind, int Start, int Length, string Text);

/// <summary>
/// The result of parsing one quick-add line: the scheduling extracted from the text, plus the
/// "clean" title with the recognized date/recurrence words removed.
/// </summary>
/// <remarks>
/// This is a pure value: the parser fills <see cref="When"/> and <see cref="Recurrence"/> from the
/// recognized phrases and leaves <see cref="Title"/> as whatever text was not consumed. A task has a
/// single date (When) — "…까지 / …마감 / N일 안에" expressions are recognized too, but they resolve to a
/// When (OnDate), not a separate deadline. When nothing is recognized (or the input is ambiguous),
/// the whole input becomes the title and the schedule slots stay empty — the parser never throws.
/// </remarks>
public sealed record ParsedQuickAdd(
    string Title,
    ScheduledWhen When,
    RecurrenceRule? Recurrence)
{
    /// <summary>
    /// True when the parser recognized an explicit placement signal for <see cref="When"/>,
    /// including an explicit Unscheduled marker such as "언젠가" or a recurrence anchor.
    /// </summary>
    public bool WhenAssigned { get; init; }

    /// <summary>
    /// True when <see cref="When"/> was recognized with an explicit time-of-day (e.g. "내일 3시"),
    /// false for a date-only recognition (e.g. "내일"). The quick-add path registers a date-only
    /// task as an all-day event. Meaningless when <see cref="WhenAssigned"/> is false.
    /// </summary>
    public bool WhenHasTime { get; init; }

    /// <summary>
    /// The recognized spans, in original-input order, that were lifted out of the title. Empty when
    /// nothing was recognized. Purely positional metadata for the inline accent and click-to-correct;
    /// the scheduling result still lives in <see cref="When"/>/<see cref="Recurrence"/>.
    /// </summary>
    public IReadOnlyList<QuickAddToken> Tokens { get; init; } = Array.Empty<QuickAddToken>();

    /// <summary>A result that carries only a title — nothing was scheduled.</summary>
    public static ParsedQuickAdd TitleOnly(string title)
        => new(title, ScheduledWhen.Unscheduled, null);
}
