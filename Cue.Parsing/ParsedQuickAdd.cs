using Cue.Domain;

namespace Cue.Parsing;

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

    /// <summary>A result that carries only a title — nothing was scheduled.</summary>
    public static ParsedQuickAdd TitleOnly(string title)
        => new(title, ScheduledWhen.Unscheduled, null);
}
