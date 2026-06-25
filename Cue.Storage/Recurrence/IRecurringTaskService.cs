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
    /// <see cref="OccurrenceStatus.Skipped"/> (no checklist snapshot) and advances the series one cycle,
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
}
