using Cue.Domain;

namespace Cue.Storage.Recurrence;

/// <summary>
/// <inheritdoc cref="IRecurringTaskService"/>
/// </summary>
/// <remarks>
/// <b>Completion-on-advance policy: method B (Logbook copy + advance), chosen as the default.</b>
/// When a repeating task is completed we keep a completed, one-off copy in the Logbook and advance
/// the original to its next cycle. This was chosen over method A (advance only, no history) because
/// a to-do app's value is partly the record of what was finished: with method A a daily habit would
/// never leave a trace in the Logbook, so "did I do it yesterday?" becomes unanswerable. The copy is
/// the cost; an accurate completion history is the benefit.
/// <para>
/// The advance is based on the completed instance's own <see cref="TaskItem.When"/> (falling back to
/// the recurrence anchor when the task carries no concrete When), <i>not</i> on the completion
/// moment. This guarantees the original moves exactly one cycle per completion and stays on the
/// rule's grid (e.g. every Monday stays a Monday), even when the task is completed late. If it lands
/// in the past it simply surfaces in Today (overdue rolls forward) until completed again.
/// </para>
/// </remarks>
public sealed class RecurringTaskService : IRecurringTaskService
{
    private readonly ITaskStore _store;

    public RecurringTaskService(ITaskStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task CompleteAsync(Guid taskId, DateTimeOffset completedAt, CancellationToken cancellationToken = default)
    {
        var task = await _store.GetAsync<TaskItem>(taskId, cancellationToken).ConfigureAwait(false);
        if (task is null || task.IsDeleted)
            return;

        // Base the advance on the instance being completed: its When if it has one, else the anchor.
        var next = task.Recurrence is { } recurrence
            ? RecurrenceCalculator.Next(recurrence, task.When.Date?.Utc ?? recurrence.Anchor.Utc)
            : null;

        if (next is null)
        {
            // Non-recurring, series exhausted, or an RRULE we couldn't evaluate: complete in place.
            // (Invalid recurrence is intentionally treated like a one-off rather than throwing.)
            task.CompletedAt = completedAt;
            await _store.SaveAsync(task, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Method B, step 1: leave a completed one-off copy of this instance in the Logbook.
        var logbookCopy = CreateLogbookCopy(task, completedAt);
        await _store.SaveAsync(logbookCopy, cancellationToken).ConfigureAwait(false);

        // Method B, step 2: advance the original to the next cycle and keep it open (alive as the
        // next instance). Only When changes; the rank is preserved and the store stamps UpdatedAt
        // (invariants 4 and 8).
        task.When = ScheduledWhen.On(next.Value, task.When.IsEvening);
        task.CompletedAt = null;
        await _store.SaveAsync(task, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A standalone, completed snapshot of the instance being finished. It is a distinct
    /// <see cref="TaskItem"/> record (its own id/file) and never recurs — a copy does not advance.
    /// </summary>
    private static TaskItem CreateLogbookCopy(TaskItem original, DateTimeOffset completedAt) => new()
    {
        // New identity: this is a separate record, not the original.
        Title = original.Title,
        Notes = original.Notes,
        CompletedAt = completedAt,
        Deadline = original.Deadline,
        When = original.When, // the instance's own scheduled date, frozen as the historical record
        Priority = original.Priority,
        ProjectId = original.ProjectId,
        SectionId = original.SectionId,
        ParentTaskId = original.ParentTaskId,
        LabelIds = new List<Guid>(original.LabelIds),

        // No recurrence on the copy: it is a frozen completion record, it does not advance.
        Recurrence = null,

        // SortOrder is meaningless for a Logbook entry — the Logbook is ordered by CompletedAt
        // descending, not by rank. We copy the original's rank verbatim rather than leaving it empty
        // (an empty rank reads as "unranked"), but nothing sorts on it here.
        SortOrder = original.SortOrder,
    };
}
