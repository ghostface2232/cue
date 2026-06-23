using Cue.Domain;

namespace Cue.Parsing;

/// <summary>
/// The reference frame a parse runs in: "now" and the user's time zone, plus the calendar helpers
/// the rules use to turn relative expressions into absolute, zone-pinned dates. Computed once per
/// parse and handed to every rule.
/// </summary>
public sealed class ParseContext
{
    public ParseContext(DateTimeOffset now, string timeZoneId, KoreanDateParserOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            throw new ArgumentException("A time zone id is required.", nameof(timeZoneId));

        Now = now;
        TimeZoneId = timeZoneId;
        Options = options ?? new KoreanDateParserOptions();
        LocalNow = ZonedDateTime.FromUtc(now, timeZoneId).ToLocal();
        Today = DateOnly.FromDateTime(LocalNow.DateTime);
    }

    /// <summary>The reference instant.</summary>
    public DateTimeOffset Now { get; }

    /// <summary>The user's time zone — every recognized date is pinned in it.</summary>
    public string TimeZoneId { get; }

    /// <summary><see cref="Now"/> projected into <see cref="TimeZoneId"/>.</summary>
    public DateTimeOffset LocalNow { get; }

    /// <summary>Runtime parser behavior toggles.</summary>
    public KoreanDateParserOptions Options { get; }

    /// <summary>The local calendar day "today" falls on.</summary>
    public DateOnly Today { get; }

    /// <summary>A zoned value for <paramref name="date"/> at the given local wall-clock time.</summary>
    public ZonedDateTime Zoned(DateOnly date, int hour = 0, int minute = 0)
        => ZonedDateTime.FromLocal(new DateTime(date.Year, date.Month, date.Day, hour, minute, 0), TimeZoneId);

    /// <summary>"Now" as a zoned value — used as a recurrence anchor for clock-relative rules.</summary>
    public ZonedDateTime ZonedNow => ZonedDateTime.FromUtc(Now, TimeZoneId);

    /// <summary>
    /// Disambiguates a bare clock hour — one typed with no AM/PM word, e.g. "오늘 3시". A to-do is
    /// rarely scheduled before dawn (a genuine early task gets written "새벽 3시"/"오전 3시"), so:
    /// <list type="bullet">
    /// <item>1–6 o'clock defaults to the afternoon (13:00–18:00) on any date.</item>
    /// <item>7–11 o'clock is a plausible morning slot (a 9am meeting), so it stays AM — unless its
    /// morning reading has already passed today, in which case it flips to PM.</item>
    /// </list>
    /// Hours with an explicit meridiem, plus noon, midnight, and the already-unambiguous afternoon
    /// hours (12–23), are returned unchanged.
    /// </summary>
    public int DisambiguateBareHour(DateOnly date, int hour, int minute, bool meridiemGiven)
    {
        if (meridiemGiven || hour is < 1 or > 11)
            return hour;
        if (hour <= 6 && Options.AutoAfternoonForBareOneToSix)
            return hour + 12;
        if (hour <= 6)
            return hour;
        return Zoned(date, hour, minute).Utc < Now ? hour + 12 : hour;
    }

    /// <summary>The next date (today allowed) that lands on <paramref name="target"/>.</summary>
    public DateOnly UpcomingWeekday(DayOfWeek target, bool allowToday = true)
    {
        var delta = ((int)target - (int)Today.DayOfWeek + 7) % 7;
        if (delta == 0 && !allowToday)
            delta = 7;
        return Today.AddDays(delta);
    }

    /// <summary>That weekday in the following ISO week (weeks start Monday).</summary>
    public DateOnly NextWeekWeekday(DayOfWeek target)
    {
        var backToMonday = ((int)Today.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        var nextMonday = Today.AddDays(-backToMonday).AddDays(7);
        var forward = ((int)target - (int)DayOfWeek.Monday + 7) % 7;
        return nextMonday.AddDays(forward);
    }

    /// <summary>The next occurrence of day-of-month <paramref name="dom"/> (this month or next).</summary>
    public DateOnly UpcomingDayOfMonth(int dom)
    {
        var (y, m) = (Today.Year, Today.Month);
        var day = Math.Min(dom, DateTime.DaysInMonth(y, m));
        var candidate = new DateOnly(y, m, day);
        if (candidate >= Today)
            return candidate;
        m++;
        if (m > 12) { m = 1; y++; }
        return new DateOnly(y, m, Math.Min(dom, DateTime.DaysInMonth(y, m)));
    }

    /// <summary>This year's <paramref name="month"/>/<paramref name="day"/>, or next year's if already past.</summary>
    public DateOnly MonthDay(int month, int day)
    {
        var y = Today.Year;
        var candidate = new DateOnly(y, month, Math.Min(day, DateTime.DaysInMonth(y, month)));
        if (candidate >= Today)
            return candidate;
        y++;
        return new DateOnly(y, month, Math.Min(day, DateTime.DaysInMonth(y, month)));
    }

    /// <summary>The last calendar day of the current month.</summary>
    public DateOnly EndOfThisMonth()
        => new(Today.Year, Today.Month, DateTime.DaysInMonth(Today.Year, Today.Month));

    /// <summary>The same day-of-month one month out (clamped to the month's length).</summary>
    public DateOnly NextMonthSameDay()
    {
        var (y, m) = (Today.Year, Today.Month + 1);
        if (m > 12) { m = 1; y++; }
        return new DateOnly(y, m, Math.Min(Today.Day, DateTime.DaysInMonth(y, m)));
    }

    /// <summary><paramref name="months"/> calendar months out, same day-of-month (clamped).</summary>
    public DateOnly AddMonths(int months)
    {
        var anchor = new DateTime(Today.Year, Today.Month, Today.Day).AddMonths(months);
        return DateOnly.FromDateTime(anchor);
    }
}
