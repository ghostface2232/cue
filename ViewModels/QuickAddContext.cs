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
        if (parsedWhen.Kind != WhenKind.Unscheduled)
            return parsedWhen;

        var localToday = TimeZoneInfo.ConvertTime(utcNow, zone).Date;
        return mode switch
        {
            TaskListMode.Today => ScheduledWhen.On(PinDay(localToday, zone)),
            TaskListMode.Upcoming => ScheduledWhen.On(PinDay(localToday.AddDays(1), zone)),
            TaskListMode.Someday => ScheduledWhen.SomeDay,
            _ => parsedWhen,
        };
    }

    private static ZonedDateTime PinDay(DateTime localDay, TimeZoneInfo zone)
        => ZonedDateTime.FromLocal(localDay.Date.AddHours(12), zone.Id);
}
