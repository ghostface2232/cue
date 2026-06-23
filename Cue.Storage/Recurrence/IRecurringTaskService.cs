using Cue.Domain;

namespace Cue.Storage.Recurrence;

/// <summary>
/// Completes a task with recurrence awareness. This is the store/logic seam the view models call so
/// that completing a repeating task advances it to its next cycle — the next-occurrence math and the
/// Ical.Net dependency stay below this interface, never in a view model (invariant 9).
/// </summary>
public interface IRecurringTaskService
{
    /// <summary>
    /// Marks the task with <paramref name="taskId"/> complete at <paramref name="completedAt"/>.
    /// <para>
    /// A non-recurring task (or one whose series has ended / whose RRULE cannot be evaluated) is
    /// completed in place: its <see cref="TaskItem.CompletedAt"/> is stamped and it is saved.
    /// </para>
    /// <para>
    /// A recurring task is handled with <b>method B</b>: a completed one-off copy is written to the
    /// Logbook and the original is advanced to its next cycle (its <see cref="TaskItem.When"/> moves
    /// forward and it stays open). See <see cref="RecurringTaskService"/> for the rationale.
    /// </para>
    /// All writes go through <see cref="ITaskStore"/>. A no-op if the task is missing or deleted.
    /// </summary>
    Task CompleteAsync(Guid taskId, DateTimeOffset completedAt, CancellationToken cancellationToken = default);
}
