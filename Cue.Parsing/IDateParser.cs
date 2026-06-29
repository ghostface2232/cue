namespace Cue.Parsing;

/// <summary>
/// Parses a single quick-add line into scheduling + a clean title. A pure service: it depends on no
/// UI or storage, reads no clock, and never throws — the caller supplies the reference "now" and the
/// user's time zone so results are deterministic and the recognized dates can be stamped correctly.
/// </summary>
public interface IDateParser
{
    /// <summary>
    /// Parses <paramref name="input"/> as of <paramref name="now"/> in <paramref name="timeZoneId"/>.
    /// Relative expressions ("내일", "다음 주 화요일") are resolved to absolute dates pinned in that
    /// zone. On no/ambiguous match, returns the whole input as the title with empty scheduling.
    /// </summary>
    ParsedQuickAdd Parse(string input, DateTimeOffset now, string timeZoneId);

    /// <summary>
    /// As <see cref="Parse(string, DateTimeOffset, string)"/>, but with <paramref name="suppressedSpans"/>
    /// — original-text spans the user has reverted. Those spans are excluded from recognition (no token,
    /// no scheduling) yet are <b>kept in the title</b>: reverting "금요일" in "금요일 회의" yields the
    /// title "금요일 회의", not "회의". Step 4 of the inline-highlight plan.
    /// </summary>
    ParsedQuickAdd Parse(string input, DateTimeOffset now, string timeZoneId, IReadOnlyList<TextSpan> suppressedSpans);
}
