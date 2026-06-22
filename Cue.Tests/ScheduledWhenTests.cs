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
        Assert.False(when.IsEvening);
        Assert.Equal(ScheduledWhen.Unscheduled, when);
    }

    [Fact]
    public void SomeDay_IsParkedWithNoDate()
    {
        var when = ScheduledWhen.SomeDay;

        Assert.Equal(WhenKind.SomeDay, when.Kind);
        Assert.False(when.HasDate);
        Assert.Null(when.Date);
    }

    [Fact]
    public void On_PinsAZonedDate()
    {
        var date = ZonedDateTime.FromLocal(new DateTime(2026, 7, 1, 9, 0, 0), "Asia/Seoul");
        var when = ScheduledWhen.On(date);

        Assert.Equal(WhenKind.OnDate, when.Kind);
        Assert.True(when.HasDate);
        Assert.Equal(date, when.Date!.Value);
        Assert.False(when.IsEvening);
    }

    // "Today" and "This Evening" are both OnDate with the current day stamped by the caller;
    // This Evening just sets the evening flag. They are not distinct stored states.
    [Fact]
    public void ThisEvening_IsAnOnDateWithTheEveningFlag()
    {
        var today = ZonedDateTime.FromLocal(new DateTime(2026, 6, 22, 0, 0, 0), "Asia/Seoul");

        var todayWhen = ScheduledWhen.On(today);
        var eveningWhen = ScheduledWhen.On(today, evening: true);

        Assert.Equal(WhenKind.OnDate, todayWhen.Kind);
        Assert.False(todayWhen.IsEvening);

        Assert.Equal(WhenKind.OnDate, eveningWhen.Kind);
        Assert.True(eveningWhen.IsEvening);
        Assert.Equal(today, eveningWhen.Date!.Value);
    }

    // The invalid combinations (OnDate without a date, a dateless state carrying a date) are
    // unrepresentable because construction is factory-only: the setters are not public, so no
    // external object initializer / `with` can assemble an inconsistent value.
    [Theory]
    [InlineData(nameof(ScheduledWhen.Kind))]
    [InlineData(nameof(ScheduledWhen.Date))]
    [InlineData(nameof(ScheduledWhen.IsEvening))]
    public void Setters_AreNotPubliclyAccessible(string propertyName)
    {
        var setter = typeof(ScheduledWhen).GetProperty(propertyName)!.SetMethod;
        Assert.True(setter is null || !setter.IsPublic, $"{propertyName} must not have a public setter");
    }
}
