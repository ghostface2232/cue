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
/// <param name="DocumentVersion">The control's text version at submit time, for stale-parse detection.</param>
/// <remarks>
/// A <c>Corrections</c> list (non-text corrections such as forcing a bare "3시" to 15:00, or toggling
/// scheduled↔deadline) is deliberately omitted for the MVP: every popover correction is currently a plain
/// text replacement that the re-parse picks up on its own. It is added when a non-text correction lands.
/// </remarks>
public sealed record QuickAddSubmission(
    string RawText,
    IReadOnlyList<TextSpan> SuppressedSpans,
    long DocumentVersion)
{
    /// <summary>A bare commit of <paramref name="rawText"/> with no suppression (the legacy/Enter path).</summary>
    public static QuickAddSubmission Plain(string rawText)
        => new(rawText ?? string.Empty, Array.Empty<TextSpan>(), 0);
}
