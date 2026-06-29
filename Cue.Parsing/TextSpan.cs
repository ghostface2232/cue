namespace Cue.Parsing;

/// <summary>
/// A half-open character range over the original (screen) input — <c>[Start, Start+Length)</c> in
/// UTF-16 code units, the same unit the token contract uses. Used to tell the parser which spans the
/// user has <i>reverted</i> (suppressed): those spans must not be recognized again, yet must remain in
/// the final title. See the inline-highlight plan, step 4.
/// </summary>
public readonly record struct TextSpan(int Start, int Length);
