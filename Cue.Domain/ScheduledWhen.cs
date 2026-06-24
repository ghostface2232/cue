namespace Cue.Domain;

/// <summary>The kind of "when" a task is scheduled for.</summary>
public enum WhenKind
{
    /// <summary>미정 — no scheduled date; the task surfaces only in "언젠가" (Anytime), never Today/Upcoming.</summary>
    Unscheduled,

    /// <summary>
    /// A concrete (zoned) date is pinned in <see cref="ScheduledWhen.Date"/>. Both "Today" and a
    /// specific future date are this kind — they differ only by what the date is, which the view
    /// compares against the current day.
    /// </summary>
    OnDate,
}

/// <summary>
/// A task's single date ("When"): when the user intends to work on it / when it is due, and the
/// basis for Today/Upcoming. There is no separate deadline — a task has exactly one date.
/// </summary>
/// <remarks>
/// There are only two stored states: <see cref="WhenKind.Unscheduled"/> (no date) and
/// <see cref="WhenKind.OnDate"/> (a concrete zoned date).
/// <para>
/// "Today" is <b>not</b> stored as a distinct state. When the user picks it, the caller stamps the
/// current day in the user's time zone and stores it as <see cref="WhenKind.OnDate"/>. The domain
/// never reads the clock; resolving today / upcoming / overdue is left to view logic that compares
/// <see cref="Date"/> against the current day.
/// </para>
/// <para>
/// A concrete <see cref="Date"/> is a user-specified date, so it stores UTC plus the original time
/// zone (<see cref="ZonedDateTime"/>). The default value is <see cref="WhenKind.Unscheduled"/>.
/// </para>
/// <para>
/// An <see cref="WhenKind.OnDate"/> is either <b>timed</b> (a meaningful wall-clock time the user set)
/// or <b>all-day</b> (종일 — a day with no meaningful time-of-day). This is carried explicitly by
/// <see cref="IsAllDay"/>, <i>not</i> inferred from a sentinel time such as 23:59: the time component of
/// an all-day <see cref="Date"/> is not meaningful and the UI hides it.
/// </para>
/// </remarks>
/// <remarks>
/// The setters are private so the only ways to build a value are the factories below
/// (<see cref="Unscheduled"/>, <see cref="On"/>) — and the struct's <c>default</c>, which is
/// <see cref="Unscheduled"/>. This makes the invalid combinations (OnDate without a date, or a
/// dateless state carrying a date) unrepresentable. The guard is pure domain logic; it pulls in no
/// serialization concern.
/// </remarks>
public readonly record struct ScheduledWhen
{
    /// <summary>Which stored state this scheduling is in.</summary>
    public WhenKind Kind { get; private init; }

    /// <summary>The concrete date, present only for <see cref="WhenKind.OnDate"/>.</summary>
    public ZonedDateTime? Date { get; private init; }

    /// <summary>
    /// 종일 — true when this is an all-day date carrying no meaningful time-of-day, so the UI shows the
    /// day alone. Only ever true for <see cref="WhenKind.OnDate"/>; always false when
    /// <see cref="WhenKind.Unscheduled"/>. This is an explicit flag, not an inference from the
    /// <see cref="Date"/>'s time component.
    /// </summary>
    public bool IsAllDay { get; private init; }

    /// <summary>미정 — the default, no scheduled date.</summary>
    public static readonly ScheduledWhen Unscheduled = default;

    /// <summary>
    /// Schedule for a concrete (zoned) date <i>with a meaningful time</i>. Use this for a specific day
    /// and for "Today" (pass the current day in the user's zone).
    /// </summary>
    public static ScheduledWhen On(ZonedDateTime date)
        => new() { Kind = WhenKind.OnDate, Date = date, IsAllDay = false };

    /// <summary>
    /// Schedule for an all-day (종일) date: a concrete (zoned) day whose time-of-day is not meaningful.
    /// The <paramref name="date"/> still pins the calendar day and zone; the UI hides the time.
    /// </summary>
    public static ScheduledWhen AllDay(ZonedDateTime date)
        => new() { Kind = WhenKind.OnDate, Date = date, IsAllDay = true };

    /// <summary>True when a concrete date is pinned. Reflects the actual presence of <see cref="Date"/>.</summary>
    public bool HasDate => Date is not null;
}
