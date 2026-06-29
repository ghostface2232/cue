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
/// <para>
/// The edit position is recovered from the strings alone (shared prefix + suffix), which is genuinely
/// ambiguous when repeated text sits at the edit boundary: "내일 내일" → "내일 내일 내일" could be an insert at
/// the front <i>or</i> the end, and the two readings place a revert on different "내일"s. Rather than pick one
/// and silently suppress the wrong word, we compute the edit both ways (prefix-first and suffix-first) and
/// treat the union of the two changed regions as the uncertain zone — any span overlapping it is dropped.
/// A span clearly before or after that zone shifts/stays unambiguously regardless of which reading is true.
/// </para>
/// </summary>
public static class SuppressionTracker
{
    /// <summary>Remaps <paramref name="spans"/> from <paramref name="oldText"/> coordinates to
    /// <paramref name="newText"/> coordinates, dropping any the edit touched, that became invalid, or that
    /// fell inside an ambiguous (repeated-boundary) edit region.</summary>
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

        // Decomposition A — maximize the shared prefix, then the shared suffix in what's left.
        var pA = 0;
        while (pA < max && oldText[pA] == newText[pA])
            pA++;
        var sA = 0;
        while (sA < max - pA && oldText[oldLen - 1 - sA] == newText[newLen - 1 - sA])
            sA++;

        // Decomposition B — maximize the shared suffix first, then the shared prefix. When repeated text
        // straddles the edit, this lands the changed region at the opposite end from A; the gap between the
        // two is exactly what the strings can't disambiguate.
        var sB = 0;
        while (sB < max && oldText[oldLen - 1 - sB] == newText[newLen - 1 - sB])
            sB++;
        var pB = 0;
        while (pB < max - sB && oldText[pB] == newText[pB])
            pB++;

        // The uncertain zone (old coordinates): the union of both readings' changed regions. Outside it the
        // edit's effect on a span is the same under either reading, so the span moves deterministically.
        var editStart = Math.Min(pA, pB);                          // earliest a change could begin
        var editOldEnd = Math.Max(oldLen - sA, oldLen - sB);       // latest the removed region could end
        var delta = newLen - oldLen;                               // inserted − removed

        var result = new List<TextSpan>(spans.Count);
        foreach (var span in spans)
        {
            if (span.Length <= 0)
                continue;

            var start = span.Start;
            var end = span.Start + span.Length;

            if (editOldEnd <= start)
            {
                // The whole uncertain zone lies before the span (incl. an insertion at its start): shift it.
                var moved = new TextSpan(start + delta, span.Length);
                if (IsValid(moved, newLen))
                    result.Add(moved);
            }
            else if (editStart >= end)
            {
                // The whole uncertain zone lies after the span (incl. an insertion at its end): unchanged.
                result.Add(span);
            }
            // else: the span overlaps the (possibly ambiguous) edit region — the user edited that word, or we
            // can't tell which repeat it now sits on; either way drop the revert rather than mis-place it.
        }

        return result;
    }

    private static bool IsValid(TextSpan span, int textLength)
        => span.Length > 0 && span.Start >= 0 && span.Start + span.Length <= textLength;
}
