using Cue.Domain;
using Cue.Parsing;

namespace Cue.ViewModels;

/// <summary>
/// What the quick-add control hands the view model on commit (step 5.2 of the inline-highlight plan).
/// The control does <i>not</i> pass a finished <see cref="ParsedQuickAdd"/>: the live parse is a
/// display-only cache and would be stale across a midnight/bare-time/timezone boundary (the parser reads
/// <c>now</c>). Instead the control passes the raw line plus the editor-held suppression state, and the
/// view model re-parses at the current clock just before saving.
/// </summary>
/// <param name="RawText">The exact visible text (untrimmed); suppression offsets index into it.</param>
/// <param name="SuppressedSpans">Original-text spans the user reverted — excluded from recognition but
/// kept in the title. Empty for a plain commit.</param>
/// <param name="Reminder">The reminder timing chosen inline in the time-token popover. This is the first
/// non-text correction carried on the submission: the parser does not (yet) recognize a reminder phrase
/// (that corpus work is deferred), so the choice can't ride the text and travels here instead. Defaults to
/// <see cref="ReminderTiming.AtTime"/> — the same default the domain uses for a task with no explicit choice
/// — and is only meaningful when the committed task turns out to be timed (the scheduler ignores it otherwise).</param>
/// <remarks>
/// Staleness across the save is handled by the view model, not carried here: the commit re-parses at the
/// current clock and clears the box only when its text still matches this submission, so a slow save can't
/// wipe a line the user kept typing. Every other popover correction is a plain text replacement the re-parse
/// picks up on its own; only <see cref="Reminder"/> needs to be carried out-of-band.
/// </remarks>
public sealed record QuickAddSubmission(
    string RawText,
    IReadOnlyList<TextSpan> SuppressedSpans,
    ReminderTiming Reminder = ReminderTiming.AtTime)
{
    /// <summary>A bare commit of <paramref name="rawText"/> with no suppression (the legacy/Enter path).</summary>
    public static QuickAddSubmission Plain(string rawText)
        => new(rawText ?? string.Empty, Array.Empty<TextSpan>());
}
