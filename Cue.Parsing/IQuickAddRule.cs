using System.Text.RegularExpressions;
using Cue.Domain;

namespace Cue.Parsing;

/// <summary>
/// One step in the parse pipeline — the boost seam. A rule exposes a <see cref="Pattern"/> and, when
/// it matches, <see cref="Extract"/> resolves the match into schedule slots. The engine then strips
/// the matched span from the working text so it never lands in the title.
/// </summary>
/// <remarks>
/// Modeled on chrono's parser list: rules run in order, each consuming what it recognizes. The
/// built-in Korean rules cover the common cases; custom rules can be supplied to
/// <see cref="KoreanDateParser"/> and run <i>first</i>, so they can add Korean expressions the
/// defaults miss or override a misrecognition before the defaults see the text.
/// </remarks>
public interface IQuickAddRule
{
    /// <summary>The expression this rule recognizes.</summary>
    Regex Pattern { get; }

    /// <summary>
    /// Resolves <paramref name="match"/> into <paramref name="result"/>. Returns <c>true</c> if it
    /// claimed the match (the engine then removes that span from the title), or <c>false</c> to
    /// decline it (e.g. the slot is already filled, or the match is a false positive), leaving the
    /// text in place. Must never throw — the engine isolates a faulty rule, but rules should be safe.
    /// </summary>
    bool Extract(Match match, ParseContext context, QuickAddResult result);
}

/// <summary>
/// The mutable accumulator the rules write into. Each slot is write-once: the first rule to claim it
/// wins, which is what lets earlier (more specific, or user-supplied) rules take precedence.
/// </summary>
public sealed class QuickAddResult
{
    /// <summary>Whether a scheduled <see cref="When"/> has been set.</summary>
    public bool WhenAssigned { get; private set; }

    /// <summary>The scheduled date, if recognized.</summary>
    public ScheduledWhen When { get; private set; } = ScheduledWhen.Unscheduled;

    /// <summary>The recurrence, if a "매일/매주/…" expression was recognized.</summary>
    public RecurrenceRule? Recurrence { get; private set; }

    /// <summary>Sets <see cref="When"/> once; returns false if it was already set.</summary>
    public bool TrySetWhen(ScheduledWhen when)
    {
        if (WhenAssigned)
            return false;
        When = when;
        WhenAssigned = true;
        return true;
    }

    /// <summary>Sets <see cref="Recurrence"/> once; returns false if it was already set.</summary>
    public bool TrySetRecurrence(RecurrenceRule recurrence)
    {
        if (Recurrence is not null)
            return false;
        Recurrence = recurrence;
        return true;
    }
}
