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
            var occurrence = CreateOccurrence(task, status, status == OccurrenceStatus.Completed ? at : null, occurrenceUtc);
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

    public Task EndSeriesAsync(Guid taskId, CancellationToken cancellationToken = default)
        => _store.MutateAsync<TaskItem>(taskId, task =>
        {
            if (task.Recurrence is null)
                return false; // not recurring — nothing to end

            // Ending the series stops the recurrence and leaves the task open as a plain one-off at its
            // current cycle. It is NOT a completion (그 표현은 현재 회차에만 쓴다): the user finishes or
            // deletes it normally afterward. Only Recurrence is cleared; the recorded cycle history stays.
            task.Recurrence = null;
            return true;
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

    /// <summary>
    /// A history record of one cycle, owned by the series via <see cref="RecurrenceOccurrence.SeriesId"/>.
    /// Its id is deterministic (not <see cref="Guid.NewGuid"/>) so a crash-retried record of the same
    /// un-advanced cycle overwrites rather than duplicates it.
    /// </summary>
    private static RecurrenceOccurrence CreateOccurrence(TaskItem series, OccurrenceStatus status, DateTimeOffset? completedAt, DateTimeOffset occurrenceUtc) => new()
    {
        Id = OccurrenceId(series.Id, occurrenceUtc),
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
