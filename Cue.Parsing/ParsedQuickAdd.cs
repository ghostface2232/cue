using Cue.Domain;

namespace Cue.Parsing;

/// <summary>
/// The result of parsing one quick-add line: the scheduling extracted from the text, plus the
/// "clean" title with the recognized date/recurrence words removed.
/// </summary>
/// <remarks>
/// This is a pure value: the parser fills <see cref="When"/>, <see cref="Deadline"/>, and
/// <see cref="Recurrence"/> from the recognized phrases and leaves <see cref="Title"/> as whatever
/// text was not consumed. When nothing is recognized (or the input is ambiguous), the whole input
/// becomes the title and the schedule slots stay empty — the parser never throws.
/// </remarks>
public sealed record ParsedQuickAdd(
    string Title,
    ScheduledWhen When,
    ZonedDateTime? Deadline,
    RecurrenceRule? Recurrence)
{
    /// <summary>A result that carries only a title — nothing was scheduled.</summary>
    public static ParsedQuickAdd TitleOnly(string title)
        => new(title, ScheduledWhen.Unscheduled, null, null);
}
