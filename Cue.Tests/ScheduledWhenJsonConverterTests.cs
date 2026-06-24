using System.Text.Json;
using Cue.Domain;
using Cue.Storage.Serialization;

namespace Cue.Tests;

public class ScheduledWhenJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = StoreSerialization.CreateOptions();

    [Fact]
    public void Unscheduled_RoundTrips()
    {
        var json = JsonSerializer.Serialize(ScheduledWhen.Unscheduled, Options);
        var back = JsonSerializer.Deserialize<ScheduledWhen>(json, Options);

        Assert.Equal(ScheduledWhen.Unscheduled, back);
    }

    [Fact]
    public void TimedOnDate_RoundTrips_AndStaysNotAllDay()
    {
        var when = ScheduledWhen.On(ZonedDateTime.FromLocal(new DateTime(2026, 7, 1, 9, 0, 0), "Asia/Seoul"));

        var json = JsonSerializer.Serialize(when, Options);
        var back = JsonSerializer.Deserialize<ScheduledWhen>(json, Options);

        Assert.Equal(when, back);
        Assert.False(back.IsAllDay);
    }

    [Fact]
    public void AllDayOnDate_RoundTrips_AndKeepsTheFlag()
    {
        var when = ScheduledWhen.AllDay(ZonedDateTime.FromLocal(new DateTime(2026, 7, 1, 0, 0, 0), "Asia/Seoul"));

        var json = JsonSerializer.Serialize(when, Options);
        Assert.Contains("\"isAllDay\": true", json);

        var back = JsonSerializer.Deserialize<ScheduledWhen>(json, Options);
        Assert.Equal(when, back);
        Assert.True(back.IsAllDay);
    }

    // Records written before the flag existed encoded all-day as a 23:59 local pin; reading must recover
    // the 종일 intent so existing data keeps hiding the time.
    [Fact]
    public void LegacyOnDate_PinnedTo2359_WithNoFlag_ReadsAsAllDay()
    {
        var date = ZonedDateTime.FromLocal(new DateTime(2026, 7, 1, 23, 59, 0), "Asia/Seoul");
        var legacy = $"{{\"kind\":\"OnDate\",\"date\":{JsonSerializer.Serialize(date, Options)}}}";

        var back = JsonSerializer.Deserialize<ScheduledWhen>(legacy, Options);

        Assert.Equal(WhenKind.OnDate, back.Kind);
        Assert.True(back.IsAllDay);
    }

    [Fact]
    public void LegacyOnDate_WithAMeaningfulTime_AndNoFlag_ReadsAsTimed()
    {
        var date = ZonedDateTime.FromLocal(new DateTime(2026, 7, 1, 9, 0, 0), "Asia/Seoul");
        var legacy = $"{{\"kind\":\"OnDate\",\"date\":{JsonSerializer.Serialize(date, Options)}}}";

        var back = JsonSerializer.Deserialize<ScheduledWhen>(legacy, Options);

        Assert.Equal(WhenKind.OnDate, back.Kind);
        Assert.False(back.IsAllDay);
    }

    // An explicit flag always wins over the legacy heuristic, even when the time happens to be 23:59.
    [Fact]
    public void ExplicitNotAllDay_At2359_IsHonoredOverTheLegacyMarker()
    {
        var when = ScheduledWhen.On(ZonedDateTime.FromLocal(new DateTime(2026, 7, 1, 23, 59, 0), "Asia/Seoul"));

        var json = JsonSerializer.Serialize(when, Options);
        var back = JsonSerializer.Deserialize<ScheduledWhen>(json, Options);

        Assert.False(back.IsAllDay);
        Assert.Equal(when, back);
    }
}
