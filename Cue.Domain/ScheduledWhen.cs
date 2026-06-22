namespace Cue.Domain;

/// <summary>The kind of "when" a task is scheduled for (Things' When semantics).</summary>
public enum WhenKind
{
    /// <summary>미정 — no scheduled date; the task does not surface in Today/Upcoming.</summary>
    Unscheduled,

    /// <summary>오늘 — show in Today. Relative; resolved against the current day by logic later.</summary>
    Today,

    /// <summary>오늘 저녁 — Things' "This Evening" bucket within Today.</summary>
    ThisEvening,

    /// <summary>특정일 — a concrete date carried in <see cref="ScheduledWhen.Date"/>.</summary>
    OnDate,

    /// <summary>언젠가 — Things' "Someday"; parked, not scheduled to a date.</summary>
    SomeDay,
}

/// <summary>
/// A task's scheduled date ("When"): when the user intends to work on it and the basis for
/// Today/Upcoming. Richer than a bare date so it can hold the relative buckets Things uses —
/// Today, This Evening, Someday — alongside a concrete date.
/// </summary>
/// <remarks>
/// Only <see cref="WhenKind.OnDate"/> carries an absolute <see cref="Date"/>; the relative kinds
/// (Today/ThisEvening/SomeDay) are resolved against the current day by logic in a later stage.
/// When a concrete date is present it is a user-specified scheduled date, so it follows the
/// project-wide rule of storing UTC plus the original time zone (<see cref="ZonedDateTime"/>).
/// The default value is <see cref="WhenKind.Unscheduled"/>.
/// </remarks>
public readonly record struct ScheduledWhen
{
    /// <summary>Which bucket this scheduling falls into.</summary>
    public WhenKind Kind { get; init; }

    /// <summary>The concrete date, set only when <see cref="Kind"/> is <see cref="WhenKind.OnDate"/>.</summary>
    public ZonedDateTime? Date { get; init; }

    /// <summary>미정 — the default, no scheduled date.</summary>
    public static readonly ScheduledWhen Unscheduled = new() { Kind = WhenKind.Unscheduled };

    /// <summary>오늘.</summary>
    public static readonly ScheduledWhen Today = new() { Kind = WhenKind.Today };

    /// <summary>오늘 저녁.</summary>
    public static readonly ScheduledWhen ThisEvening = new() { Kind = WhenKind.ThisEvening };

    /// <summary>언젠가.</summary>
    public static readonly ScheduledWhen SomeDay = new() { Kind = WhenKind.SomeDay };

    /// <summary>특정일 — schedule for a concrete (zoned) date.</summary>
    public static ScheduledWhen On(ZonedDateTime date) => new() { Kind = WhenKind.OnDate, Date = date };

    /// <summary>True for anything other than <see cref="WhenKind.Unscheduled"/>.</summary>
    public bool IsScheduled => Kind != WhenKind.Unscheduled;
}
