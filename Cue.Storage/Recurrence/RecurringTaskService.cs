using System.Security.Cryptography;
using System.Text;
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
/// <para>
/// <b>Crash safety: the two saves are made idempotent, not transactional.</b> Completion writes two
/// separate records — the Logbook copy, then the advanced original — and the store has no cross-file
/// transaction, so a crash <i>between</i> the writes leaves the copy on disk while the original is
/// still due (un-advanced). To keep that path from minting a duplicate copy on the retried
/// completion, the copy's id is <i>derived</i> from the original's id plus the completed instance's
/// occurrence date (see <see cref="LogbookCopyId"/>) rather than random. Re-completing the same
/// still-un-advanced instance therefore reproduces the same id and overwrites the orphaned copy in
/// place; the advance then runs. Once the original has advanced, the next cycle's occurrence yields a
/// different id, so each cycle still leaves its own distinct history entry.
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
        // This same occurrence instant also keys the Logbook copy's deterministic id below.
        var occurrenceUtc = task.When.Date?.Utc ?? task.Recurrence?.Anchor.Utc;
        var next = task.Recurrence is { } recurrence
            ? RecurrenceCalculator.Next(recurrence, occurrenceUtc!.Value)
            : null;

        if (next is null)
        {
            // Non-recurring, series exhausted, or an RRULE we couldn't evaluate: complete in place.
            // (Invalid recurrence is intentionally treated like a one-off rather than throwing.)
            task.CompletedAt = completedAt;
            await _store.SaveAsync(task, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Method B, step 1: leave a completed one-off copy of this instance in the Logbook. The copy's
        // id is derived from (original id, this occurrence) so a retry after a crash between this save
        // and the advance overwrites the orphaned copy instead of duplicating it (see class remarks).
        var logbookCopy = CreateLogbookCopy(task, completedAt, LogbookCopyId(task.Id, occurrenceUtc!.Value));
        await _store.SaveAsync(logbookCopy, cancellationToken).ConfigureAwait(false);

        // Method B, step 2: advance the original to the next cycle and keep it open (alive as the
        // next instance). Only When changes; the rank is preserved and the store stamps UpdatedAt
        // (invariants 4 and 8).
        task.When = ScheduledWhen.On(next.Value);
        task.CompletedAt = null;
        await _store.SaveAsync(task, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A standalone, completed snapshot of the instance being finished. It is a distinct
    /// <see cref="TaskItem"/> record (its own id/file) and never recurs — a copy does not advance.
    /// </summary>
    private static TaskItem CreateLogbookCopy(TaskItem original, DateTimeOffset completedAt, Guid id) => new()
    {
        // Separate record from the original, but a deterministic identity (not Guid.NewGuid) so a
        // crash-retried completion of the same instance overwrites this copy rather than duplicating it.
        Id = id,
        Title = original.Title,
        Notes = original.Notes,
        CompletedAt = completedAt,
        When = original.When, // the instance's own date, frozen as the historical record
        Priority = original.Priority,
        TaskGroupId = original.TaskGroupId,
        ParentTaskId = original.ParentTaskId,
        TagIds = new List<Guid>(original.TagIds),

        // No recurrence on the copy: it is a frozen completion record, it does not advance.
        Recurrence = null,

        // SortOrder is meaningless for a Logbook entry — the Logbook is ordered by CompletedAt
        // descending, not by rank. We copy the original's rank verbatim rather than leaving it empty
        // (an empty rank reads as "unranked"), but nothing sorts on it here.
        SortOrder = original.SortOrder,
    };

    /// <summary>
    /// A stable, name-based id for the Logbook copy of one completed occurrence, derived from the
    /// original task id and that occurrence's UTC instant. Two completions of the <i>same</i>
    /// un-advanced instance map to the same id (so a crash-retry overwrites rather than duplicates),
    /// while each advanced cycle has a distinct occurrence and so a distinct copy. Deterministic and
    /// process-independent: a SHA-256 digest of the two values folded into a GUID.
    /// </summary>
    private static Guid LogbookCopyId(Guid originalId, DateTimeOffset occurrenceUtc)
    {
        var name = $"cue/logbook-copy/{originalId:N}/{occurrenceUtc.UtcDateTime.Ticks}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        return new Guid(hash.AsSpan(0, 16));
    }
}
