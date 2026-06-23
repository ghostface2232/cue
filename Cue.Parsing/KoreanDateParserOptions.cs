namespace Cue.Parsing;

/// <summary>Runtime options for the Korean quick-add parser.</summary>
public sealed class KoreanDateParserOptions
{
    /// <summary>
    /// When true, bare 1-6 o'clock expressions are treated as afternoon (13:00-18:00).
    /// </summary>
    public bool AutoAfternoonForBareOneToSix { get; init; } = true;
}
