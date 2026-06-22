using Cue.Domain;

namespace Cue.Tests;

public class ScheduledWhenTests
{
    [Fact]
    public void Default_IsUnscheduled()
    {
        ScheduledWhen when = default;

        Assert.Equal(WhenKind.Unscheduled, when.Kind);
        Assert.False(when.IsScheduled);
        Assert.Null(when.Date);
        Assert.Equal(ScheduledWhen.Unscheduled, when);
    }

    [Theory]
    [InlineData(WhenKind.Today)]
    [InlineData(WhenKind.ThisEvening)]
    [InlineData(WhenKind.SomeDay)]
    public void RelativeBuckets_CarryNoDate(WhenKind kind)
    {
        var when = kind switch
        {
            WhenKind.Today => ScheduledWhen.Today,
            WhenKind.ThisEvening => ScheduledWhen.ThisEvening,
            WhenKind.SomeDay => ScheduledWhen.SomeDay,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        Assert.Equal(kind, when.Kind);
        Assert.True(when.IsScheduled);
        Assert.Null(when.Date);
    }

    [Fact]
    public void OnDate_CarriesAZonedDate()
    {
        var date = ZonedDateTime.FromLocal(new DateTime(2026, 7, 1, 9, 0, 0), "Asia/Seoul");
        var when = ScheduledWhen.On(date);

        Assert.Equal(WhenKind.OnDate, when.Kind);
        Assert.True(when.IsScheduled);
        Assert.NotNull(when.Date);
        Assert.Equal(date, when.Date!.Value);
    }
}
