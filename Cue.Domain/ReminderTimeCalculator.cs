namespace Cue.Domain;

/// <summary>Pure calculation of the single notification time derived from a task's schedule.</summary>
public static class ReminderTimeCalculator
{
    /// <summary>
    /// Returns the notification time in the scheduled date's original-zone offset, or <c>null</c>
    /// when the schedule is unscheduled or all-day, or the reminder is disabled.
    /// </summary>
    public static DateTimeOffset? Calculate(ScheduledWhen when, ReminderTiming reminder)
    {
        if (when.Kind != WhenKind.OnDate || when.IsAllDay || when.Date is not { } date ||
            reminder == ReminderTiming.None)
        {
            return null;
        }

        var offset = reminder switch
        {
            ReminderTiming.AtTime => TimeSpan.Zero,
            ReminderTiming.TenMinutesBefore => TimeSpan.FromMinutes(10),
            ReminderTiming.OneHourBefore => TimeSpan.FromHours(1),
            ReminderTiming.OneDayBefore => TimeSpan.FromHours(24),
            _ => throw new ArgumentOutOfRangeException(nameof(reminder), reminder, null),
        };

        return date.ToLocal() - offset;
    }
}
