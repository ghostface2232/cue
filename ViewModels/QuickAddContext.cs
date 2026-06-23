using Cue.Domain;

namespace Cue.ViewModels;

/// <summary>Applies the current list's placement only when the user did not type an explicit When.</summary>
public static class QuickAddContext
{
    public static ScheduledWhen Apply(
        ScheduledWhen parsedWhen,
        TaskListMode mode,
        DateTimeOffset utcNow,
        TimeZoneInfo zone)
    {
        // A typed date wins outright.
        if (parsedWhen.Kind != WhenKind.Unscheduled)
            return parsedWhen;

        // With no typed date, only the Today list pins an actual day. Every other list (Upcoming
        // included — it names no specific date) leaves the task Unscheduled, so a dateless quick-add
        // lands in "언젠가" (Anytime).
        if (mode != TaskListMode.Today)
            return parsedWhen;

        var localToday = TimeZoneInfo.ConvertTime(utcNow, zone).Date;
        return ScheduledWhen.On(PinDay(localToday, zone));
    }

    private static ZonedDateTime PinDay(DateTime localDay, TimeZoneInfo zone)
        => ZonedDateTime.FromLocal(localDay.Date.AddHours(12), zone.Id);
}
