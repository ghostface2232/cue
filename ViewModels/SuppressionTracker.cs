using Cue.Parsing;

namespace Cue.ViewModels;

/// <summary>
/// Editor-side state helper for revert (suppression) tracking — step 4.3 of the inline-highlight plan.
/// The parser is stateless, so the editor must remember which spans the user reverted and move them as
/// the text is edited.
/// <para>
/// <see cref="Reproject"/> assumes a single contiguous edit between two snapshots (true for
/// keystroke-level changes): a span entirely before the edit shifts by its length delta, a span entirely
/// after it is unchanged, and a span whose own range the edit touched is dropped — the user edited that
/// word, so the revert no longer applies. A span deleted outright disappears the same way. Offsets are
/// UTF-16 code units, matching <see cref="TextSpan"/> and the token contract. Pure and never throws.
/// </para>
/// </summary>
public static class SuppressionTracker
{
    /// <summary>Remaps <paramref name="spans"/> from <paramref name="oldText"/> coordinates to
    /// <paramref name="newText"/> coordinates, dropping any the edit touched or that became invalid.</summary>
    public static IReadOnlyList<TextSpan> Reproject(IReadOnlyList<TextSpan> spans, string oldText, string newText)
    {
        if (spans.Count == 0)
            return spans;

        oldText ??= string.Empty;
        newText ??= string.Empty;
        if (oldText == newText)
            return spans; // no edit — every span keeps its coordinates

        var oldLen = oldText.Length;
        var newLen = newText.Length;
        var max = Math.Min(oldLen, newLen);

        // Single contiguous edit: shared prefix p, shared suffix s, the middle is the replaced region.
        var p = 0;
        while (p < max && oldText[p] == newText[p])
            p++;

        var s = 0;
        while (s < max - p && oldText[oldLen - 1 - s] == newText[newLen - 1 - s])
            s++;

        var editStart = p;             // first changed index (old coordinates)
        var editOldEnd = oldLen - s;   // exclusive end of the removed region (old coordinates)
        var delta = newLen - oldLen;   // inserted − removed

        var result = new List<TextSpan>(spans.Count);
        foreach (var span in spans)
        {
            if (span.Length <= 0)
                continue;

            var start = span.Start;
            var end = span.Start + span.Length;

            if (editOldEnd <= start)
            {
                // Edit lies entirely before the span (incl. an insertion right at its start): shift it.
                var moved = new TextSpan(start + delta, span.Length);
                if (IsValid(moved, newLen))
                    result.Add(moved);
            }
            else if (editStart >= end)
            {
                // Edit lies entirely after the span (incl. an insertion right at its end): unchanged.
                result.Add(span);
            }
            // else: the edit overlaps the span's own range — the user edited that word; drop the revert.
        }

        return result;
    }

    private static bool IsValid(TextSpan span, int textLength)
        => span.Length > 0 && span.Start >= 0 && span.Start + span.Length <= textLength;
}
