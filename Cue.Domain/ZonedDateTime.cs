namespace Cue.Domain;

/// <summary>
/// A point in time stored as UTC plus the time zone it was originally entered in.
/// </summary>
/// <remarks>
/// Storing the instant (UTC) <i>and</i> the original zone id — rather than a bare local
/// time or a fixed offset — keeps DST correct: the offset for a given instant is recomputed
/// from the zone's rules. Values are rendered in local time only at display
/// (<see cref="ToLocal"/>). This is also what feeds last-write-wins reconciliation later.
/// <para>
/// The zone id may be an IANA id ("Asia/Seoul") or a Windows id ("Korea Standard Time");
/// .NET resolves both on Windows via ICU.
/// </para>
/// </remarks>
public readonly record struct ZonedDateTime
{
    /// <summary>The instant, always in UTC (offset zero).</summary>
    public DateTimeOffset Utc { get; init; }

    /// <summary>The time zone this value was originally expressed in.</summary>
    public string TimeZoneId { get; init; }

    public ZonedDateTime(DateTimeOffset utc, string timeZoneId)
    {
        Utc = utc.ToUniversalTime();
        TimeZoneId = timeZoneId;
    }

    /// <summary>
    /// Build from a wall-clock time as read in <paramref name="timeZoneId"/>
    /// (e.g. the user typed "tomorrow 9am" while in Seoul).
    /// </summary>
    public static ZonedDateTime FromLocal(DateTime wallClock, string timeZoneId)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var unspecified = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, tz);
        return new ZonedDateTime(new DateTimeOffset(utc, TimeSpan.Zero), timeZoneId);
    }

    /// <summary>Build from a known UTC instant tagged with the zone to display it in.</summary>
    public static ZonedDateTime FromUtc(DateTimeOffset utc, string timeZoneId)
        => new(utc, timeZoneId);

    /// <summary>The same instant projected into <see cref="TimeZoneId"/> for display.</summary>
    public DateTimeOffset ToLocal()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        return TimeZoneInfo.ConvertTime(Utc, tz);
    }
}
