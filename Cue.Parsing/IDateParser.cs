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
}
