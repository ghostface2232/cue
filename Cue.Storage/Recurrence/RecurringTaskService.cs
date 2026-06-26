using System.Security.Cryptography;
using System.Text;
using Cue.Domain;

namespace Cue.Storage.Recurrence;

/// <summary>
/// <inheritdoc cref="IRecurringTaskService"/>
/// </summary>
/// <remarks>
/// <b>Occurrence-record model.</b> Performing a recurring task's current cycle does not complete the
/// task: it writes a <see cref="RecurrenceOccurrence"/> owned by the series (via
/// <see cref="RecurrenceOccurrence.SeriesId"/>) and advances the original to its next cycle, which
/// stays open. The series' own <see cref="TaskItem.CompletedAt"/> is set only when the series is ended
/// (<see cref="EndSeriesAsync"/>) or its rule is exhausted. This replaces the former method-B "Logbook
/// copy" approach, where each cycle was a standalone completed <see cref="TaskItem"/>: history is now
/// queryable by series and each cycle keeps its own status and frozen checklist.
/// <para>
/// The advance is based on the completed instance's own <see cref="TaskItem.When"/> (falling back to
/// the recurrence anchor when the task carries no concrete When), <i>not</i> on the completion moment,
/// so the original moves exactly one cycle and stays on the rule's grid (e.g. every Monday stays a
/// Monday) even when completed late. If it lands in the past it surfaces in Today (overdue rolls
/// forward) until performed again. An all-day (종일) cycle stays all-day across the advance.
/// </para>
/// <para>
/// <b>Crash safety: the two writes are made idempotent, not transactional.</b> A cycle writes the
/// occurrence record, then advances the original; a crash between them leaves the occurrence on disk
/// while the original is still due. To keep a retry from minting a duplicate, the occurrence id is
/// <i>derived</i> from the series id plus the cycle's occurrence instant (see <see cref="OccurrenceId"/>)
/// rather than random — re-recording the same un-advanced cycle reproduces the same id and overwrites
/// the orphan in place; the advance then runs. Once advanced, the next cycle has a different occurrence
/// instant and so a distinct record.
/// </para>
/// </remarks>
public sealed class RecurringTaskService : IRecurringTaskService
{
    private readonly ITaskStore _store;

    public RecurringTaskService(ITaskStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    public Task<DateOnly?> CompleteAsync(Guid taskId, DateTimeOffset completedAt, CancellationToken cancellationToken = default)
        => RecordCurrentCycleAsync(taskId, OccurrenceStatus.Completed, completedAt, cancellationToken);

    public Task<DateOnly?> SkipAsync(Guid taskId, DateTimeOffset skippedAt, CancellationToken cancellationToken = default)
        => RecordCurrentCycleAsync(taskId, OccurrenceStatus.Skipped, skippedAt, cancellationToken);

    /// <summary>
    /// Shared body for performing (<see cref="OccurrenceStatus.Completed"/>) and skipping
    /// (<see cref="OccurrenceStatus.Skipped"/>) the current cycle: record the cycle, then advance the
    /// series (or end it in place if the rule has no further cycle).
    /// </summary>
    private async Task<DateOnly?> RecordCurrentCycleAsync(Guid taskId, OccurrenceStatus status, DateTimeOffset at, CancellationToken cancellationToken)
    {
        // Captured inside the transaction and surfaced to the caller so it can tell a "rolled to the next
        // cycle" action apart from a terminal one (non-recurring complete, exhausted series, or no-op).
        DateOnly? nextOccurrence = null;

        // The whole action runs under the store's write lock so the read, the occurrence write, and the
        // advance commit together and cannot interleave with a concurrent save (a detail edit, a list
        // toggle). The two writes still run record-then-advance, so the crash-idempotency in the class
        // remarks holds — a crash between them leaves an overwritable orphan occurrence.
        await _store.RunInTransactionAsync(async (tx, ct) =>
        {
            var task = await tx.GetAsync<TaskItem>(taskId, ct).ConfigureAwait(false);
            if (task is null || task.IsDeleted)
                return;

            if (task.Recurrence is null)
            {
                // Non-recurring: there is no cycle to record. A complete finishes it in place; a skip is a
                // no-op (you cannot skip a one-off).
                if (status == OccurrenceStatus.Completed)
                {
                    task.CompletedAt = at;
                    await tx.SaveAsync(task, ct).ConfigureAwait(false);
                }
                return;
            }

            // Base the advance on the instance being acted on: its When if it has one, else the anchor.
            // This same occurrence instant keys the occurrence record's deterministic id below.
            var occurrenceUtc = task.When.Date?.Utc ?? task.Recurrence.Anchor.Utc;
            var next = RecurrenceCalculator.Next(task.Recurrence, occurrenceUtc);

            if (next is null)
            {
                // The rule has no further cycle (UNTIL/COUNT exhausted, or it can't be evaluated): the
                // series ends naturally. Complete the original in place — exactly the one-off completion
                // path, so the final cycle is the terminal Logbook record rather than a separate occurrence.
                task.CompletedAt = at;
                await tx.SaveAsync(task, ct).ConfigureAwait(false);
                return;
            }

            // Record this advancing cycle as a history entry owned by the series. A completed cycle freezes
            // the ticked checklist; a skipped/missed cycle keeps no snapshot. (Save before the advance so a
            // crash between the two leaves an overwritable orphan — see the class remarks.)
            //
            // The id is normally the cycle's deterministic id so a crash-retry overwrites the orphan rather
            // than duplicating. But once a cycle's record has been tombstoned — an undo rolled that
            // completion back (see UndoCompletionAsync) — the store refuses to revive that id, and reviving
            // it would also be wrong: this is a brand-new completion of the now-current cycle, not the same
            // record. So when the deterministic id is a tombstone, mint a fresh id for the new record. The
            // tombstone stays dead and the index still shows exactly one live record for the date.
            var occurrenceId = OccurrenceId(task.Id, occurrenceUtc);
            if (await tx.GetAsync<RecurrenceOccurrence>(occurrenceId, ct).ConfigureAwait(false) is { IsDeleted: true })
                occurrenceId = Guid.NewGuid();
            var occurrence = CreateOccurrence(task, occurrenceId, status, status == OccurrenceStatus.Completed ? at : null);
            await tx.SaveAsync(occurrence, ct).ConfigureAwait(false);

            // Surface the advanced cycle's local date (matching how the index projects WhenDate) so the
            // caller can show a "다음: …" cue and refresh the row in place instead of folding it away.
            nextOccurrence = DateOnly.FromDateTime(next.Value.ToLocal().DateTime);

            // Advance the original to the next cycle and keep it open. Only When changes; the rank is
            // preserved and the store stamps UpdatedAt. The checklist is the recurring procedure for the
            // cycle, so it resets to unchecked here — the cycle's ticked state was frozen on the occurrence
            // record above. Preserve all-day (종일) across the advance so a 종일 series stays 종일 every cycle
            // (otherwise the first cycle would silently turn it into a timed task).
            task.When = task.When.IsAllDay
                ? ScheduledWhen.AllDay(next.Value)
                : ScheduledWhen.On(next.Value);
            task.CompletedAt = null;
            foreach (var item in task.Checklist) item.IsChecked = false;
            await tx.SaveAsync(task, ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        return nextOccurrence;
    }

    public Task EndSeriesAsync(Guid taskId, DateTimeOffset completedAt, CancellationToken cancellationToken = default)
        => _store.RunInTransactionAsync(async (tx, ct) =>
        {
            var task = await tx.GetAsync<TaskItem>(taskId, ct).ConfigureAwait(false);
            if (task is null || task.IsDeleted || task.Recurrence is null)
                return;

            // Ending the series is the only way a recurring task itself becomes completed: stamp
            // CompletedAt so it leaves the active lists and lands in the Logbook. The rule is kept on the
            // (now completed) record as historical context; it never advances again because IsCompleted.
            task.CompletedAt = completedAt;
            await tx.SaveAsync(task, ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task UpdateOccurrenceStatusAsync(Guid occurrenceId, OccurrenceStatus status, CancellationToken cancellationToken = default)
        => _store.MutateAsync<RecurrenceOccurrence>(occurrenceId, occurrence =>
        {
            if (occurrence.Status == status)
                return false; // nothing to persist
            occurrence.Status = status;
            // Editing a recorded cycle never touches the series' When, so the next scheduled cycle is
            // unaffected. Clear the completion instant when a cycle is reclassified away from Completed.
            if (status != OccurrenceStatus.Completed)
                occurrence.CompletedAt = null;
            return true;
        }, cancellationToken);

    public async Task<IReadOnlyList<DateOnly>> GetUpcomingOccurrencesAsync(Guid taskId, int count, CancellationToken cancellationToken = default)
    {
        if (count <= 0)
            return Array.Empty<DateOnly>();

        var task = await _store.GetAsync<TaskItem>(taskId, cancellationToken).ConfigureAwait(false);
        if (task is null || task.IsDeleted || task.IsCompleted || task.Recurrence is null)
            return Array.Empty<DateOnly>();

        return ProjectUpcomingOccurrences(task.Recurrence, task.When, count);
    }

    public IReadOnlyList<DateOnly> ProjectUpcomingOccurrences(RecurrenceRule recurrence, ScheduledWhen currentWhen, int count)
    {
        ArgumentNullException.ThrowIfNull(recurrence);
        if (count <= 0)
            return Array.Empty<DateOnly>();

        // Walk the rule forward from the current cycle, collecting each next local date. Every step seeds
        // the search from the previous occurrence's own instant, so the dates stay on the rule's grid
        // (e.g. every Monday stays a Monday) rather than drifting off the clock.
        var cursor = currentWhen.Date?.Utc ?? recurrence.Anchor.Utc;
        var dates = new List<DateOnly>(count);
        for (var i = 0; i < count; i++)
        {
            var next = RecurrenceCalculator.Next(recurrence, cursor);
            if (next is null)
                break; // series exhausted (UNTIL/COUNT) — no further cycle to project
            dates.Add(DateOnly.FromDateTime(next.Value.ToLocal().DateTime));
            cursor = next.Value.Utc;
        }
        return dates;
    }

    public async Task<bool> UndoCompletionAsync(Guid taskId, Guid occurrenceId, DateTimeOffset undoneAt, CancellationToken cancellationToken = default)
    {
        var rolledBack = false;

        await _store.RunInTransactionAsync(async (tx, ct) =>
        {
            var task = await tx.GetAsync<TaskItem>(taskId, ct).ConfigureAwait(false);
            if (task is null || task.IsDeleted || task.IsCompleted || task.Recurrence is null)
                return;

            var occurrence = await tx.GetAsync<RecurrenceOccurrence>(occurrenceId, ct).ConfigureAwait(false);
            if (occurrence is null || occurrence.IsDeleted
                || occurrence.SeriesId != task.Id
                || occurrence.Status != OccurrenceStatus.Completed)
                return;

            // Guard to the latest completion only: this record's own next scheduled cycle must be exactly
            // the series' current When (it is the immediate predecessor of the live cycle). Any other
            // record is a history correction, not an undo, and is left untouched — UpdateOccurrenceStatus
            // handles those. (The instant round-trips for all-day cycles too: AllDay keeps the time.)
            var occurrenceUtc = occurrence.When.Date?.Utc ?? task.Recurrence.Anchor.Utc;
            var next = RecurrenceCalculator.Next(task.Recurrence, occurrenceUtc);
            if (next is null || task.When.Date is not { } currentWhen || next.Value.Utc != currentWhen.Utc)
                return;

            // Roll the series back to this cycle: restore its frozen When (keeping all-day), reopen it, and
            // re-tick the checklist exactly as it was when the cycle was completed. The cycle is the live
            // current one again, so its history record is tombstoned.
            task.When = occurrence.When;
            task.CompletedAt = null;
            RestoreChecklist(task, occurrence.ChecklistSnapshot);
            await tx.SaveAsync(task, ct).ConfigureAwait(false);

            occurrence.DeletedAt = undoneAt;
            await tx.SaveAsync(occurrence, ct).ConfigureAwait(false);

            rolledBack = true;
        }, cancellationToken).ConfigureAwait(false);

        return rolledBack;
    }

    /// <summary>Re-applies a completed cycle's frozen checklist state onto the series' live checklist,
    /// matching items by id (best-effort — items the series has since dropped are ignored). Used by undo
    /// so rolling a completion back restores the work that was ticked that cycle.</summary>
    private static void RestoreChecklist(TaskItem series, List<ChecklistItem> snapshot)
    {
        if (snapshot.Count == 0)
            return;
        var checkedById = snapshot.ToDictionary(item => item.Id, item => item.IsChecked);
        foreach (var item in series.Checklist)
            if (checkedById.TryGetValue(item.Id, out var isChecked))
                item.IsChecked = isChecked;
    }

    /// <summary>
    /// A history record of one cycle, owned by the series via <see cref="RecurrenceOccurrence.SeriesId"/>.
    /// The caller resolves <paramref name="id"/> — the cycle's deterministic id in the normal case (so a
    /// crash-retried record of the same un-advanced cycle overwrites rather than duplicates), or a fresh id
    /// when that deterministic id has been tombstoned by an undo.
    /// </summary>
    private static RecurrenceOccurrence CreateOccurrence(TaskItem series, Guid id, OccurrenceStatus status, DateTimeOffset? completedAt) => new()
    {
        Id = id,
        SeriesId = series.Id,
        // The cycle's own scheduled date, frozen. Falls back to the anchor when the series carries no
        // concrete When, preserving the all-day flag either way.
        When = series.When.HasDate
            ? series.When
            : (series.When.IsAllDay ? ScheduledWhen.AllDay(series.Recurrence!.Anchor) : ScheduledWhen.On(series.Recurrence!.Anchor)),
        Status = status,
        CompletedAt = completedAt,
        // Freeze the checklist as it was ticked, for a completed cycle only — a deep copy (new item
        // instances), independent of the series' reset below. Skipped/missed cycles keep no snapshot.
        ChecklistSnapshot = status == OccurrenceStatus.Completed
            ? series.Checklist.Select(item => new ChecklistItem { Id = item.Id, Title = item.Title, IsChecked = item.IsChecked }).ToList()
            : new List<ChecklistItem>(),
    };

    /// <summary>
    /// A stable, name-based id for one cycle's occurrence record, derived from the series id and that
    /// cycle's UTC instant. Two records of the <i>same</i> un-advanced cycle map to the same id (so a
    /// crash-retry overwrites rather than duplicates), while each advanced cycle has a distinct instant
    /// and so a distinct record. Deterministic and process-independent: a SHA-256 digest folded into a GUID.
    /// </summary>
    private static Guid OccurrenceId(Guid seriesId, DateTimeOffset occurrenceUtc)
    {
        var name = $"cue/recurrence-occurrence/{seriesId:N}/{occurrenceUtc.UtcDateTime.Ticks}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        return new Guid(hash.AsSpan(0, 16));
    }
}
