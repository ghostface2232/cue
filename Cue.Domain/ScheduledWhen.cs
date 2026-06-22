namespace Cue.Domain;

/// <summary>The kind of "when" a task is scheduled for (Things' When semantics).</summary>
public enum WhenKind
{
    /// <summary>미정 — no scheduled date; the task does not surface in Today/Upcoming.</summary>
    Unscheduled,

    /// <summary>
    /// A concrete (zoned) date is pinned in <see cref="ScheduledWhen.Date"/>. Both "Today" and a
    /// specific future date are this kind — they differ only by what the date is, which the view
    /// compares against the current day.
    /// </summary>
    OnDate,

    /// <summary>언젠가 — Things' "Someday"; parked, with no date.</summary>
    SomeDay,
}

/// <summary>
/// A task's scheduled date ("When"): when the user intends to work on it, and the basis for
/// Today/Upcoming.
/// </summary>
/// <remarks>
/// There are only three stored states: <see cref="WhenKind.Unscheduled"/> (no date),
/// <see cref="WhenKind.OnDate"/> (a concrete zoned date, optionally flagged for the evening), and
/// <see cref="WhenKind.SomeDay"/> (parked, no date).
/// <para>
/// "Today" and "This Evening" are <b>not</b> stored as distinct states. When the user picks them,
/// the caller stamps the current day in the user's time zone and stores it as
/// <see cref="WhenKind.OnDate"/> — "This Evening" being the same date with
/// <see cref="IsEvening"/> set. The domain never reads the clock; resolving today / upcoming /
/// overdue is left to view logic that compares <see cref="Date"/> against the current day.
/// </para>
/// <para>
/// A concrete <see cref="Date"/> is a user-specified scheduled date, so it stores UTC plus the
/// original time zone (<see cref="ZonedDateTime"/>). The default value is
/// <see cref="WhenKind.Unscheduled"/>.
/// </para>
/// </remarks>
/// <remarks>
/// The setters are private so the only ways to build a value are the factories below
/// (<see cref="Unscheduled"/>, <see cref="SomeDay"/>, <see cref="On"/>) — and the struct's
/// <c>default</c>, which is <see cref="Unscheduled"/>. This makes the invalid combinations
/// (OnDate without a date, or a dateless state carrying a date / evening flag) unrepresentable.
/// The guard is pure domain logic; it pulls in no serialization concern.
/// </remarks>
public readonly record struct ScheduledWhen
{
    /// <summary>Which stored state this scheduling is in.</summary>
    public WhenKind Kind { get; private init; }

    /// <summary>The concrete date, present only for <see cref="WhenKind.OnDate"/>.</summary>
    public ZonedDateTime? Date { get; private init; }

    /// <summary>
    /// Things' "This Evening" — an evening marker on an <see cref="WhenKind.OnDate"/> value. Only
    /// ever set when a date is present.
    /// </summary>
    public bool IsEvening { get; private init; }

    /// <summary>미정 — the default, no scheduled date.</summary>
    public static readonly ScheduledWhen Unscheduled = default;

    /// <summary>언젠가.</summary>
    public static readonly ScheduledWhen SomeDay = new() { Kind = WhenKind.SomeDay };

    /// <summary>
    /// Schedule for a concrete (zoned) date. Use this for a specific day, for "Today" (pass the
    /// current day in the user's zone), and for "This Evening" (the current day with
    /// <paramref name="evening"/> set).
    /// </summary>
    public static ScheduledWhen On(ZonedDateTime date, bool evening = false)
        => new() { Kind = WhenKind.OnDate, Date = date, IsEvening = evening };

    /// <summary>True when a concrete date is pinned. Reflects the actual presence of <see cref="Date"/>.</summary>
    public bool HasDate => Date is not null;
}
