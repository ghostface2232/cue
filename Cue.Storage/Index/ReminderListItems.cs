using Cue.Domain;

namespace Cue.Storage.Index;

/// <summary>
/// One timed, non-recurring task the notification scheduler may need to reserve a toast for — the
/// index projection that replaces reading every task file on each reconcile pass.
/// </summary>
/// <remarks>
/// Everything the scheduler needs to build a <c>ScheduledNotification</c> is here, so the one-off half
/// of the reconcile loop touches no file at all: the reconstructed <see cref="When"/> feeds
/// <see cref="ReminderTimeCalculator"/> (still the single source of the timing rule) and
/// <see cref="TaskGroupName"/> comes from the same LEFT JOIN the list queries use.
/// <para>
/// The query is a coarse pre-filter — it narrows by calendar day, which is cheap and index-backed —
/// and the caller re-applies the exact delivery-instant window. Widening the SQL range can therefore
/// never change behavior, only how many rows get discarded in memory.
/// </para>
/// </remarks>
public sealed record ReminderCandidate(
    Guid Id,
    string Title,
    string? TaskGroupName,
    ScheduledWhen When,
    ReminderTiming Reminder);

/// <summary>
/// A live, reminder-bearing task reduced to its id and whether it repeats. Two callers need this
/// un-windowed view and neither needs the payload: the scheduler's stale-toast sweep (which asks
/// "does this delivered toast still refer to unresolved work?") and the recurring source (which uses
/// the ids to load only the series files it actually needs, instead of the whole folder).
/// </summary>
/// <param name="Id">The task record's id.</param>
/// <param name="IsRecurring">True when the task carries a recurrence rule. The rule itself is not
/// indexed, so a recurring series still has to be loaded by id to project its occurrences — but only
/// the recurring ones, which are a small fraction of a real list.</param>
public sealed record ReminderTaskRef(Guid Id, bool IsRecurring);
