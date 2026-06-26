using Cue.Domain;

namespace Cue.Storage.Recurrence;

/// <summary>
/// Completes a task with recurrence awareness, and is the store/logic seam the view models call for
/// every recurring-task lifecycle action (perform a cycle, skip a cycle, end the series, edit a past
/// cycle). The next-occurrence math and the Ical.Net dependency stay below this interface, never in a
/// view model (invariant 9).
/// </summary>
public interface IRecurringTaskService
{
    /// <summary>
    /// Records the <i>current cycle</i> of the task with <paramref name="taskId"/> as performed at
    /// <paramref name="completedAt"/>.
    /// <para>
    /// A non-recurring task is completed in place: its <see cref="TaskItem.CompletedAt"/> is stamped and
    /// it moves to the Logbook (its check <i>is</i> its completion).
    /// </para>
    /// <para>
    /// A recurring task is <b>not</b> completed: the current cycle is written as a
    /// <see cref="RecurrenceOccurrence"/> (<see cref="OccurrenceStatus.Completed"/>, with the checklist
    /// frozen) owned by the series, and the original is advanced one cycle (its <see cref="TaskItem.When"/>
    /// moves forward, its checklist resets) and stays open. A recurring task's own
    /// <see cref="TaskItem.CompletedAt"/> is set only by <see cref="EndSeriesAsync"/>. If the rule has no
    /// further cycle (UNTIL/COUNT exhausted, or it can't be evaluated) the series ends naturally — the
    /// original is completed in place — instead of advancing.
    /// </para>
    /// All writes go through <see cref="ITaskStore"/>. A no-op if the task is missing or deleted.
    /// </summary>
    /// <returns>
    /// The advanced cycle's <b>next occurrence</b> as a local date when a recurring task rolled forward,
    /// or <c>null</c> when the task was completed in place (non-recurring, series ended/exhausted, or the
    /// missing/deleted no-op). The caller uses this to refresh the row in place rather than fold it away.
    /// </returns>
    Task<DateOnly?> CompleteAsync(Guid taskId, DateTimeOffset completedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the current cycle of the recurring task with <paramref name="taskId"/> as
    /// <see cref="OccurrenceStatus.Missed"/> (no checklist snapshot) and advances the series one cycle,
    /// exactly like <see cref="CompleteAsync"/> but without performing the cycle. A no-op for a
    /// non-recurring task (nothing to skip), or a missing/deleted one.
    /// </summary>
    /// <returns>The next occurrence's local date when the series rolled forward, else <c>null</c>.</returns>
    Task<DateOnly?> SkipAsync(Guid taskId, DateTimeOffset skippedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends the recurring series with <paramref name="taskId"/>: stamps the original's
    /// <see cref="TaskItem.CompletedAt"/> at <paramref name="completedAt"/> so it leaves the active lists
    /// and lands in the Logbook. This is the <i>only</i> path that completes a recurring task. The
    /// recurrence rule is left on the (now completed) record as historical context; the cycle history is
    /// untouched. A no-op if the task is missing, deleted, or not recurring.
    /// </summary>
    Task EndSeriesAsync(Guid taskId, DateTimeOffset completedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-classifies a single past cycle (<paramref name="occurrenceId"/>) to <paramref name="status"/>.
    /// Touches only that <see cref="RecurrenceOccurrence"/> record — never the series' schedule — so a
    /// correction to history leaves the next scheduled cycle exactly where it was. A no-op if the
    /// occurrence is missing or deleted.
    /// </summary>
    Task UpdateOccurrenceStatusAsync(Guid occurrenceId, OccurrenceStatus status, CancellationToken cancellationToken = default);

    /// <summary>
    /// The next <paramref name="count"/> scheduled cycles strictly after the recurring task's current
    /// cycle (its <see cref="TaskItem.When"/>), as local dates oldest-first — the future the timeline
    /// renders dimmed ahead of the current cycle. Fewer than <paramref name="count"/> (or none) when the
    /// rule is exhausted (UNTIL/COUNT). Empty for a non-recurring, completed, missing, or deleted task,
    /// or a non-positive count. Pure read — computes from the rule and writes nothing.
    /// </summary>
    Task<IReadOnlyList<DateOnly>> GetUpcomingOccurrencesAsync(Guid taskId, int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// The next <paramref name="count"/> scheduled cycles strictly after <paramref name="currentWhen"/>,
    /// projected directly from <paramref name="recurrence"/> and that When — as local dates oldest-first.
    /// Unlike <see cref="GetUpcomingOccurrencesAsync"/> this reads nothing from the store: it lets a caller
    /// (the detail panel) preview 현재 및 예정 일정 the moment 반복 is turned on or its cycle/date is
    /// changed, before the edit is even saved. Falls back to the rule's anchor when <paramref name="currentWhen"/>
    /// carries no concrete date. Fewer than <paramref name="count"/> (or none) when the rule is exhausted
    /// (UNTIL/COUNT) or can't be evaluated; empty for a non-positive count. Keeps RRULE evaluation in the
    /// storage layer (invariant 9) so a view model never touches Ical.Net. Pure — writes nothing.
    /// </summary>
    IReadOnlyList<DateOnly> ProjectUpcomingOccurrences(RecurrenceRule recurrence, ScheduledWhen currentWhen, int count);

    /// <summary>
    /// Undoes the most recent completion of a recurring series: rolls the series back so the cycle the
    /// <paramref name="occurrenceId"/> record stands for becomes the live current cycle again — open and
    /// incomplete, with its frozen checklist state restored — and tombstones that occurrence record.
    /// <para>
    /// Strictly guarded to the <i>latest</i> completion: it commits only when the record is a
    /// <see cref="OccurrenceStatus.Completed"/> cycle of this series whose next scheduled cycle is exactly
    /// the series' current <see cref="TaskItem.When"/> (i.e. it is the immediate predecessor). Any other
    /// record — an older cycle, a missed one, or one whose successor isn't the current cycle — is
    /// left untouched so history corrections go through <see cref="UpdateOccurrenceStatusAsync"/> instead.
    /// A no-op if the task or occurrence is missing, deleted, completed (series ended), or not recurring.
    /// </para>
    /// All writes commit atomically under the store's transaction.
    /// </summary>
    /// <returns><c>true</c> when the series was rolled back, <c>false</c> when the guard declined.</returns>
    Task<bool> UndoCompletionAsync(Guid taskId, Guid occurrenceId, DateTimeOffset undoneAt, CancellationToken cancellationToken = default);
}
