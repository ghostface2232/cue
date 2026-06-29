namespace Cue.Parsing;

/// <summary>Runtime options for the Korean quick-add parser.</summary>
public sealed class KoreanDateParserOptions
{
    /// <summary>
    /// When true, bare 1-6 o'clock expressions are treated as afternoon (13:00-18:00).
    /// </summary>
    public bool AutoAfternoonForBareOneToSix { get; init; } = true;

    /// <summary>
    /// How a parsed ISO week number that already lies in the past resolves: when true it rolls forward to
    /// that week of the next year; when false (the default) it stays in the current ISO week-numbering year
    /// even if the resulting date is before today. Only consulted by the week-number rule.
    /// </summary>
    public bool WeekNumberRollsForwardWhenPast { get; init; }
}
