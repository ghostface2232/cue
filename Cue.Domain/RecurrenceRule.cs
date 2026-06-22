namespace Cue.Domain;

/// <summary>
/// A simple recurrence specification ("every 2 weeks", "every month until …").
/// </summary>
/// <remarks>
/// Deliberately minimal for the foundation phase — enough to express the common cases without
/// committing to a full RRULE engine. It can grow (by-weekday, by-month-day, count limits)
/// without changing the records that reference it.
/// </remarks>
public sealed class RecurrenceRule
{
    /// <summary>The unit of repetition.</summary>
    public RecurrenceFrequency Frequency { get; set; }

    /// <summary>Repeat every N units (every 1 day, every 2 weeks, …). Must be ≥ 1.</summary>
    public int Interval { get; set; } = 1;

    /// <summary>Whether the next occurrence follows the calendar or the completion time.</summary>
    public RecurrenceMode Mode { get; set; } = RecurrenceMode.FixedSchedule;

    /// <summary>Optional end of the series. <c>null</c> means it repeats indefinitely.</summary>
    public ZonedDateTime? Until { get; set; }
}
