using Cue.Domain;
using Cue.Storage;
using Cue.Storage.Recurrence;

namespace Cue.Services;

/// <summary>What a toast's 할 일 완료 button actually did.</summary>
internal enum ToastCompletionOutcome
{
    /// <summary>The activation performed the cycle (or completed the task), so open lists and the
    /// navigation badges are now stale and must be refreshed.</summary>
    Completed,

    /// <summary>The activation targeted work that had already been resolved somewhere else, so nothing was
    /// written. The stale notification has been retired and the user has been told why the button did not
    /// complete anything; no list refresh is warranted because no record changed.</summary>
    AlreadyResolved,
}

/// <summary>
/// The headless half of a toast's 할 일 완료 button: it runs with no window, so every branch has to finish
/// the interaction on its own — either by writing the completion or by explaining, through the notification
/// channel itself, why it didn't.
/// </summary>
/// <remarks>
/// <para>
/// A toast's activation payload names a task and, for a recurring series, one specific cycle
/// (<see cref="ToastActivationPayload.OccurrenceId"/>). Both can go out of date between delivery and the
/// click, because a delivered toast sits in the notification center indefinitely while the record behind it
/// keeps moving: the task can be completed or deleted in-app, and a recurring series advances past the very
/// cycle the toast names as soon as that cycle is performed or skipped. Completing on a stale payload would
/// stamp the wrong thing — a cycle the series has already left behind, or a second completion over a task
/// that is done — so the guards below refuse it. That refusal is correct and is not the part that changes.
/// </para>
/// <para>
/// What it cannot be is silent. The button press is the user's whole interaction here; returning with no
/// write and no feedback leaves a notification the user just pressed 완료 on and nothing observable
/// happening, which reads as a broken button rather than as "this was already done". So every refusal
/// retires the tag that produced it (the reconcile loop's sweep is periodic, and the app may well have been
/// closed since the work was resolved, so the entry can still be sitting in the notification center),
/// reconciles the rest of the center in the same pass, and shows a short toast saying the notification was
/// a stale one. The reconcile is what the caller would otherwise have skipped entirely on this path.
/// </para>
/// </remarks>
internal sealed class ToastCompletionService
{
    private readonly ITaskStore _store;
    private readonly IRecurringTaskService _recurrence;
    private readonly NotificationScheduler _scheduler;
    private readonly IToastPresenter _toasts;
    private readonly TimeProvider _clock;

    public ToastCompletionService(
        ITaskStore store,
        IRecurringTaskService recurrence,
        NotificationScheduler scheduler,
        IToastPresenter toasts,
        TimeProvider clock)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _recurrence = recurrence ?? throw new ArgumentNullException(nameof(recurrence));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _toasts = toasts ?? throw new ArgumentNullException(nameof(toasts));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<ToastCompletionOutcome> CompleteAsync(
        ToastActivationPayload payload,
        CancellationToken cancellationToken = default)
    {
        var task = await _store.GetAsync<TaskItem>(payload.TaskId, cancellationToken).ConfigureAwait(false);
        if (task is null || task.IsDeleted || task.IsCompleted)
        {
            // Gone, tombstoned, or finished — including a recurring series that was ended, which is the one
            // path that stamps a recurring task's own CompletedAt.
            return await RetireStaleAsync(
                payload,
                task?.Title,
                "이미 완료했거나 삭제한 할 일이라 알림만 정리했어요.",
                cancellationToken).ConfigureAwait(false);
        }

        if (payload.OccurrenceId is { } expectedOccurrenceId)
        {
            // The series' current cycle, derived exactly as the recurring source derived the tag. A series
            // that lost its rule has no cycle to match at all, so it is stale for the same reason.
            var currentOccurrenceId = task.Recurrence is { } rule
                ? RecurrenceOccurrenceId.From(task.Id, task.When.Date?.Utc ?? rule.Anchor.Utc)
                : (Guid?)null;

            if (currentOccurrenceId != expectedOccurrenceId)
            {
                return await RetireStaleAsync(
                    payload,
                    task.Title,
                    "이미 처리한 회차라서 알림만 정리했어요.",
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await _recurrence.CompleteAsync(payload.TaskId, _clock.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        // A headless process exits before the save-event debounce can elapse, so reconcile explicitly after
        // the shared completion path to remove this task's now-stale scheduled toast.
        await _scheduler.ReconcileAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return ToastCompletionOutcome.Completed;
    }

    /// <summary>Retires the notification this activation came from and tells the user it was stale.</summary>
    /// <param name="title">The task's title, or null when its record is gone — the toast's first line is
    /// the task title everywhere else, so it stays that here and only falls back when there is none.</param>
    private async Task<ToastCompletionOutcome> RetireStaleAsync(
        ToastActivationPayload payload,
        string? title,
        string reason,
        CancellationToken cancellationToken)
    {
        // Retire this tag directly rather than leaving it to the sweep below: the reconcile pass revokes a
        // delivered toast by comparing the notification center against live truth, and it would reach this
        // one too, but only the direct call is guaranteed to be about the exact entry the user pressed.
        var tag = payload.OccurrenceId is { } occurrenceId
            ? NotificationScheduler.TagForOccurrence(occurrenceId)
            : NotificationScheduler.TagForTask(payload.TaskId);
        _toasts.CancelScheduled(tag);
        _toasts.RemoveFromHistory(tag);

        // Whatever left this one behind almost certainly left siblings behind too — the app has been closed
        // (so no periodic pass ran) or the work was resolved between passes. Sweep the whole center once.
        await _scheduler.ReconcileAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        // Feedback carries no completion arguments, so pressing it just opens the app; it is deliberately
        // untagged, which also keeps it outside the managed-tag set the reconcile sweep operates on.
        if (title is null)
            _toasts.Show("지난 알림을 정리했어요", "이미 완료했거나 삭제한 할 일이에요.");
        else
            _toasts.Show(title, reason);

        return ToastCompletionOutcome.AlreadyResolved;
    }
}
