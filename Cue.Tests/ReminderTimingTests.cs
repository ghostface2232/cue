using System.Text.Json;
using Cue.Domain;
using Cue.Storage.Serialization;

namespace Cue.Tests;

public class ReminderTimingTests
{
    private static readonly JsonSerializerOptions JsonOptions = StoreSerialization.CreateOptions();

    [Fact]
    public void LegacyJson_WithoutReminder_DeserializesAsAtTime()
    {
        const string json = """
            {
              "title": "기존 작업"
            }
            """;

        var task = JsonSerializer.Deserialize<TaskItem>(json, JsonOptions);

        Assert.NotNull(task);
        Assert.Equal(ReminderTiming.AtTime, task.Reminder);
    }

    [Theory]
    [InlineData(ReminderTiming.AtTime, 0)]
    [InlineData(ReminderTiming.TenMinutesBefore, 10)]
    [InlineData(ReminderTiming.OneHourBefore, 60)]
    [InlineData(ReminderTiming.OneDayBefore, 24 * 60)]
    public void Calculate_ReturnsExpectedTimeForEachEnabledTiming(
        ReminderTiming timing,
        int minutesBefore)
    {
        var scheduled = new DateTimeOffset(2026, 7, 2, 9, 0, 0, TimeSpan.FromHours(9));
        var when = ScheduledWhen.On(
            ZonedDateTime.FromLocal(scheduled.DateTime, "Asia/Seoul"));

        var result = ReminderTimeCalculator.Calculate(when, timing);

        Assert.Equal(scheduled.AddMinutes(-minutesBefore), result);
    }

    [Fact]
    public void Calculate_OneDayBefore_IsExactly24HoursAndPreviousDaySameTime()
    {
        var scheduled = new DateTimeOffset(2026, 7, 2, 9, 0, 0, TimeSpan.FromHours(9));
        var when = ScheduledWhen.On(
            ZonedDateTime.FromLocal(scheduled.DateTime, "Asia/Seoul"));

        var result = ReminderTimeCalculator.Calculate(when, ReminderTiming.OneDayBefore);

        Assert.Equal(new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.FromHours(9)), result);
        Assert.Equal(TimeSpan.FromHours(24), scheduled - result!.Value);
    }

    [Fact]
    public void Calculate_None_ReturnsNull()
    {
        var when = ScheduledWhen.On(
            ZonedDateTime.FromLocal(new DateTime(2026, 7, 2, 9, 0, 0), "Asia/Seoul"));

        Assert.Null(ReminderTimeCalculator.Calculate(when, ReminderTiming.None));
    }

    [Fact]
    public void Calculate_AllDayAndUnscheduled_ReturnNull()
    {
        var date = ZonedDateTime.FromLocal(new DateTime(2026, 7, 2), "Asia/Seoul");

        Assert.Null(ReminderTimeCalculator.Calculate(
            ScheduledWhen.AllDay(date),
            ReminderTiming.AtTime));
        Assert.Null(ReminderTimeCalculator.Calculate(
            ScheduledWhen.Unscheduled,
            ReminderTiming.AtTime));
    }

    [Fact]
    public void Calculate_PreservesTheScheduledZoneOffset()
    {
        var when = ScheduledWhen.On(
            ZonedDateTime.FromLocal(new DateTime(2026, 7, 2, 9, 0, 0), "America/New_York"));

        var result = ReminderTimeCalculator.Calculate(when, ReminderTiming.OneHourBefore);

        Assert.Equal(new DateTimeOffset(2026, 7, 2, 8, 0, 0, TimeSpan.FromHours(-4)), result);
    }
}
