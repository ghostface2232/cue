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
    /// The token kind to tag a claimed match with when it carries no finer-grained <c>date</c>/
    /// <c>time</c>/<c>recur</c> capture groups to split on (e.g. "언젠가", "3시간 후"). Defaults to
    /// <see cref="QuickAddTokenKind.Date"/>; rules whose whole match is a time or someday marker override it.
    /// </summary>
    QuickAddTokenKind TokenKind => QuickAddTokenKind.Date;

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

    /// <summary>
    /// Whether the recognized <see cref="When"/> carried an explicit time-of-day (e.g. "3시"), as
    /// opposed to a date only (e.g. "3월 15일"). A date-only result is treated as an all-day event by
    /// the quick-add path. Meaningless when <see cref="WhenAssigned"/> is false.
    /// </summary>
    public bool WhenHasTime { get; private set; }

    /// <summary>The scheduled date, if recognized.</summary>
    public ScheduledWhen When { get; private set; } = ScheduledWhen.Unscheduled;

    /// <summary>The recurrence, if a "매일/매주/…" expression was recognized.</summary>
    public RecurrenceRule? Recurrence { get; private set; }

    /// <summary>
    /// Whether the recurrence's anchor carries a meaningful time-of-day — true when the user typed an
    /// explicit clock time ("매일 9시") or the frequency is sub-daily ("5분마다"/"3시간마다", whose anchor is
    /// the current instant). False for a bare date-only recurrence ("매주 금요일"), whose anchor sits at
    /// 00:00 only to be evaluable. Drives whether the anchor, when promoted to <see cref="When"/>, is
    /// treated as timed or as an all-day (종일) date. Meaningless when <see cref="Recurrence"/> is null.
    /// </summary>
    public bool RecurrenceAnchorHasTime { get; private set; }

    /// <summary>Sets <see cref="When"/> once; returns false if it was already set.
    /// <paramref name="hasTime"/> records whether an explicit clock time was part of the recognition.</summary>
    public bool TrySetWhen(ScheduledWhen when, bool hasTime = false)
    {
        if (WhenAssigned)
            return false;
        When = when;
        WhenHasTime = hasTime;
        WhenAssigned = true;
        return true;
    }

    /// <summary>Sets <see cref="Recurrence"/> once; returns false if it was already set.
    /// <paramref name="anchorHasTime"/> records whether the anchor carries a meaningful time-of-day
    /// (see <see cref="RecurrenceAnchorHasTime"/>).</summary>
    public bool TrySetRecurrence(RecurrenceRule recurrence, bool anchorHasTime = false)
    {
        if (Recurrence is not null)
            return false;
        Recurrence = recurrence;
        RecurrenceAnchorHasTime = anchorHasTime;
        return true;
    }
}
