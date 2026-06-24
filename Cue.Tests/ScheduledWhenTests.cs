using Cue.Domain;

namespace Cue.Tests;

public class ScheduledWhenTests
{
    [Fact]
    public void Default_IsUnscheduled()
    {
        ScheduledWhen when = default;

        Assert.Equal(WhenKind.Unscheduled, when.Kind);
        Assert.False(when.HasDate);
        Assert.Null(when.Date);
        Assert.Equal(ScheduledWhen.Unscheduled, when);
    }

    [Fact]
    public void On_PinsAZonedDate()
    {
        var date = ZonedDateTime.FromLocal(new DateTime(2026, 7, 1, 9, 0, 0), "Asia/Seoul");
        var when = ScheduledWhen.On(date);

        Assert.Equal(WhenKind.OnDate, when.Kind);
        Assert.True(when.HasDate);
        Assert.Equal(date, when.Date!.Value);
    }

    // 종일 is carried by an explicit flag, not inferred from the date's time component.
    [Fact]
    public void On_IsNotAllDay()
    {
        var when = ScheduledWhen.On(ZonedDateTime.FromLocal(new DateTime(2026, 7, 1, 23, 59, 0), "Asia/Seoul"));

        Assert.False(when.IsAllDay); // even pinned to 23:59 — the flag, not the time, decides
    }

    [Fact]
    public void AllDay_IsAnOnDateFlaggedAllDay()
    {
        var date = ZonedDateTime.FromLocal(new DateTime(2026, 7, 1, 0, 0, 0), "Asia/Seoul");
        var when = ScheduledWhen.AllDay(date);

        Assert.Equal(WhenKind.OnDate, when.Kind);
        Assert.True(when.HasDate);
        Assert.True(when.IsAllDay);
        Assert.Equal(date, when.Date!.Value);
    }

    [Fact]
    public void Unscheduled_IsNeverAllDay()
    {
        Assert.False(ScheduledWhen.Unscheduled.IsAllDay);
        Assert.False(default(ScheduledWhen).IsAllDay);
    }

    // The all-day flag participates in value equality, so a timed and an all-day date never compare equal.
    [Fact]
    public void TimedAndAllDay_OnTheSameInstant_AreNotEqual()
    {
        var date = ZonedDateTime.FromLocal(new DateTime(2026, 7, 1, 0, 0, 0), "Asia/Seoul");

        Assert.NotEqual(ScheduledWhen.On(date), ScheduledWhen.AllDay(date));
    }

    // "Today" is just an OnDate with the current day stamped by the caller — not a distinct state.
    [Fact]
    public void Today_IsAnOnDateForTheCurrentDay()
    {
        var today = ZonedDateTime.FromLocal(new DateTime(2026, 6, 22, 0, 0, 0), "Asia/Seoul");

        var todayWhen = ScheduledWhen.On(today);

        Assert.Equal(WhenKind.OnDate, todayWhen.Kind);
        Assert.Equal(today, todayWhen.Date!.Value);
    }

    // The invalid combinations (OnDate without a date, a dateless state carrying a date) are
    // unrepresentable because construction is factory-only: the setters are not public, so no
    // external object initializer / `with` can assemble an inconsistent value.
    [Theory]
    [InlineData(nameof(ScheduledWhen.Kind))]
    [InlineData(nameof(ScheduledWhen.Date))]
    [InlineData(nameof(ScheduledWhen.IsAllDay))]
    public void Setters_AreNotPubliclyAccessible(string propertyName)
    {
        var setter = typeof(ScheduledWhen).GetProperty(propertyName)!.SetMethod;
        Assert.True(setter is null || !setter.IsPublic, $"{propertyName} must not have a public setter");
    }
}
