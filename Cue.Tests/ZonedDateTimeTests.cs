using Cue.Domain;

namespace Cue.Tests;

public class ZonedDateTimeTests
{
    // Korea has no DST, so a Seoul wall-clock maps to a fixed +09:00 offset.
    [Fact]
    public void FromLocal_StoresUtc_AndRoundTripsBackToSameWallClock()
    {
        var wall = new DateTime(2026, 6, 22, 9, 0, 0);
        var zoned = ZonedDateTime.FromLocal(wall, "Asia/Seoul");

        // Stored as UTC (Seoul is +9, so 09:00 local == 00:00 UTC).
        Assert.Equal(TimeSpan.Zero, zoned.Utc.Offset);
        Assert.Equal(new DateTime(2026, 6, 22, 0, 0, 0), zoned.Utc.UtcDateTime);

        // Rendered back into the original zone, the wall-clock time is preserved.
        var local = zoned.ToLocal();
        Assert.Equal(TimeSpan.FromHours(9), local.Offset);
        Assert.Equal(wall, local.DateTime);
    }

    // Same instant, displayed in two zones, yields two different wall-clock times.
    [Fact]
    public void ToLocal_ProjectsTheSameInstantIntoTheStoredZone()
    {
        var instant = new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero);

        var seoul = ZonedDateTime.FromUtc(instant, "Asia/Seoul").ToLocal();
        var utc = ZonedDateTime.FromUtc(instant, "UTC").ToLocal();

        Assert.Equal(9, seoul.Hour);
        Assert.Equal(0, utc.Hour);
        // Different offsets, identical underlying instant.
        Assert.Equal(seoul.UtcDateTime, utc.UtcDateTime);
    }

    // DST correctness: a US Eastern summer time carries the daylight (-04:00) offset,
    // proving the offset is derived from the zone's rules, not a frozen value.
    [Fact]
    public void FromLocal_AppliesDaylightSavingForZonesThatObserveIt()
    {
        var summerWall = new DateTime(2026, 7, 1, 12, 0, 0);
        var eastern = ZonedDateTime.FromLocal(summerWall, "America/New_York");

        var local = eastern.ToLocal();
        Assert.Equal(TimeSpan.FromHours(-4), local.Offset); // EDT, not EST (-5)
        Assert.Equal(summerWall, local.DateTime);
    }

    [Fact]
    public void Constructor_NormalizesInputToUtc()
    {
        var offset = new DateTimeOffset(2026, 6, 22, 9, 0, 0, TimeSpan.FromHours(9));
        var zoned = new ZonedDateTime(offset, "Asia/Seoul");

        Assert.Equal(TimeSpan.Zero, zoned.Utc.Offset);
        Assert.Equal(offset.UtcDateTime, zoned.Utc.UtcDateTime);
    }
}
