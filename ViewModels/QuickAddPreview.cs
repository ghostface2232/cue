namespace Cue.ViewModels;

/// <summary>
/// What the live quick-add line resolves to, for the token popover (step 5). <see cref="DateText"/>/
/// <see cref="TimeText"/> are display strings for the header (e.g. "6월 30일 (화)", "오후 3:00").
/// <see cref="Hour"/>/<see cref="Minute"/> are the resolved 24-hour clock, used to compute the time
/// token's relative alternatives (±1 hour, AM/PM flip). All fields are null when the line has no resolved
/// date/time. Recurrence/someday headers reuse the token's own phrase, so they aren't carried here.
/// </summary>
public sealed record QuickAddPreview(string? DateText, string? TimeText, int? Hour, int? Minute);
