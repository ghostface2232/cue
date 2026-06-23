using Cue.Domain;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;
using RecurrenceRule = Cue.Domain.RecurrenceRule;

namespace Cue.Storage.Recurrence;

/// <summary>
/// Computes the next occurrence of a <see cref="RecurrenceRule"/> (an RFC 5545 RRULE string plus a
/// zoned anchor). This is the store/logic layer's recurrence engine; it is the <i>only</i> place
/// Ical.Net is used, keeping that dependency out of the domain and the view models (invariant 9).
/// </summary>
/// <remarks>
/// Ical.Net is used purely as a calculator: the RRULE string and the anchor are handed to it, an
/// instant is read back, and the result is re-expressed as our own <see cref="ZonedDateTime"/>. We
/// never persist Ical.Net objects or let them replace the stored format.
/// <para>
/// The computation runs in the anchor's <i>original time zone</i> (the anchor's wall-clock time is
/// the DTSTART), so a weekly/monthly rule that crosses a DST boundary keeps its local wall-clock
/// time and only the UTC instant shifts. Computing in UTC alone would silently move the local time
/// by an hour across the boundary.
/// </para>
/// </remarks>
public static class RecurrenceCalculator
{
    // Bounds the evaluator when a rule's constraints rarely (or never) match — e.g.
    // FREQ=MONTHLY;BYMONTHDAY=31 skips the short months, and an impossible rule matches nothing.
    // Without a limit such a rule would search forever; with it the evaluator gives up and we report
    // "no next occurrence" rather than hanging.
    private static readonly EvaluationOptions Options = new() { MaxUnmatchedIncrementsLimit = 1000 };

    /// <summary>
    /// The first occurrence strictly after <paramref name="after"/>, expressed in the anchor's
    /// original time zone. Returns <c>null</c> when the series has no such occurrence (an exhausted
    /// <c>UNTIL</c>/<c>COUNT</c>) or when the rule cannot be evaluated at all — an unrecognized or
    /// malformed RRULE, or an unknown time zone. A null result is the caller's cue to treat the task
    /// as non-recurring rather than to fail.
    /// </summary>
    public static ZonedDateTime? Next(RecurrenceRule recurrence, DateTimeOffset after)
    {
        ArgumentNullException.ThrowIfNull(recurrence);

        var tzId = recurrence.Anchor.TimeZoneId;
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);

            // DTSTART is the anchor's wall-clock time in its own zone.
            var anchorWall = TimeZoneInfo.ConvertTime(recurrence.Anchor.Utc, tz).DateTime;
            var calendarEvent = new CalendarEvent
            {
                Start = new CalDateTime(DateTime.SpecifyKind(anchorWall, DateTimeKind.Unspecified), tzId, hasTime: true),
                RecurrenceRule = new RecurrencePattern(recurrence.Rule),
            };

            // Start the search at the same instant, expressed as wall-clock in the anchor's zone, and
            // then keep only occurrences strictly after it (GetOccurrences is inclusive of the start).
            var afterWall = TimeZoneInfo.ConvertTime(after, tz).DateTime;
            var searchStart = new CalDateTime(DateTime.SpecifyKind(afterWall, DateTimeKind.Unspecified), tzId, hasTime: true);

            foreach (var occurrence in calendarEvent.GetOccurrences(searchStart, Options))
            {
                var instant = new DateTimeOffset(
                    DateTime.SpecifyKind(occurrence.Period.StartTime.AsUtc, DateTimeKind.Utc), TimeSpan.Zero);
                if (instant > after)
                    return ZonedDateTime.FromUtc(instant, tzId);
            }

            return null; // series exhausted (UNTIL/COUNT) — no further occurrence
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unrecognized/invalid RRULE, unknown zone, or an evaluator failure: don't blow up — the
            // caller treats this task as a plain, non-recurring one.
            System.Diagnostics.Debug.WriteLine(
                $"[Cue] Recurrence rule '{recurrence.Rule}' could not be evaluated: {ex.Message}");
            return null;
        }
    }
}
