using Cue.Domain;
using Cue.Storage;
using Cue.Storage.Index;
using Cue.Storage.Recurrence;

namespace Cue.Services;

/// <summary>
/// The recurring half of the reconcile loop's expected set — the extension point
/// <see cref="NotificationScheduler"/> leaves open in the foundation (<see cref="IRecurringNotificationSource"/>).
/// For every recurring series it enumerates the occurrences that fire inside the rolling window and emits
/// one <see cref="ScheduledNotification"/> per occurrence, tagged by the occurrence's deterministic id.
/// </summary>
/// <remarks>
/// <para>
/// Occurrence-instant math is delegated to <see cref="IRecurringTaskService.ProjectOccurrencesInWindow"/>,
/// so the RRULE + anchor logic is reused, never duplicated, and stays in the storage layer (invariant 9).
/// </para>
/// <para>
/// The series' <see cref="TaskItem.Reminder"/> is a single setting applied uniformly to every occurrence —
/// there is no per-occurrence reminder. Because each occurrence's tag and its completion payload's
/// <c>occurrenceId</c> are derived from the same instant the completion path uses
/// (<see cref="RecurrenceOccurrenceId.From"/>), completing one cycle — which advances the series past that
/// occurrence — removes exactly that occurrence from the expected set, and the scheduler's diff cancels
/// only its reservation. Completing a cycle never touches the rest of the series.
/// </para>
/// <para>
/// The expected set is windowed by <i>delivery</i> time, so an occurrence whose toast has already fired
/// has left it — which is why the source also reports <see cref="RecurringNotificationSet.LiveTags"/>: the
/// tags of every projected cycle, delivered or not. A delivered toast is only in the notification center,
/// never in the schedule queue, so the scheduler's ordinary diff cannot see it; that live set is what tells
/// the scheduler a fired cycle is still unresolved (keep it) or has been completed, skipped, or deleted
/// (retire it). It is deliberately un-windowed at the near end for the same reason the one-off live-task
/// set is: a fired-but-unfinished reminder must survive its own delivery time.
/// </para>
/// <para>
/// An RRULE is not indexed (a list row only needs the <c>is_recurring</c> boolean), so unlike the one-off
/// half of the loop this source does have to read record files. It reads only the ones it needs: the index
/// names the live, reminder-bearing recurring series and those files are loaded by id, rather than
/// enumerating the whole <c>tasks/</c> folder on every reconcile pass. Group names come from the index too.
/// </para>
/// </remarks>
internal sealed class RecurringNotificationSource : IRecurringNotificationSource
{
    // The largest pre-reminder lead (OneDayBefore). An occurrence whose instant sits up to this far beyond
    // the window can still deliver inside it (delivery = instant − lead), so project that much past the
    // window and let the per-occurrence delivery check decide — matching the one-off path's window test.
    private static readonly TimeSpan MaxReminderLead = TimeSpan.FromHours(24);

    private readonly ITaskStore _store;
    private readonly ITaskIndex _index;
    private readonly IRecurringTaskService _recurrence;

    public RecurringNotificationSource(ITaskStore store, ITaskIndex index, IRecurringTaskService recurrence)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _recurrence = recurrence ?? throw new ArgumentNullException(nameof(recurrence));
    }

    public async Task<RecurringNotificationSet> GetExpectedAsync(
        DateTimeOffset now, DateTimeOffset windowEnd, CancellationToken cancellationToken = default)
    {
        // The index names the series worth looking at (alive, open, reminder not None); the per-task guard
        // below still applies the full predicate, so narrowing here can only save reads, never change the
        // outcome. A series whose file has since vanished simply yields null and is skipped.
        var refs = await _index.GetReminderTaskRefsAsync(cancellationToken).ConfigureAwait(false);
        var tasks = new List<TaskItem>();
        foreach (var reference in refs)
        {
            if (!reference.IsRecurring) continue;
            if (await _store.GetAsync<TaskItem>(reference.Id, cancellationToken).ConfigureAwait(false) is { } task)
                tasks.Add(task);
        }

        // Group names for the toast's second line (non-deleted only), same as the one-off path.
        var groupNames = (await _index.GetTaskGroupsAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(group => group.Id, group => group.Name);

        var projectionEnd = windowEnd + MaxReminderLead;
        var expected = new List<ScheduledNotification>();
        var liveTags = new HashSet<string>(StringComparer.Ordinal);

        foreach (var task in tasks)
        {
            // Only recurring series belong here — one-off tasks are the base scheduler's job. All-day series
            // carry no individual toast (the opt-in daily briefing covers them); None disables the series.
            // A series excluded here reports no live tags either, so switching its reminder off (or ending
            // or deleting the series) also retires whatever it had already delivered.
            if (task.IsDeleted || task.IsCompleted || task.Recurrence is null ||
                task.Reminder == ReminderTiming.None || task.When.IsAllDay)
            {
                continue;
            }

            string? groupName = task.TaskGroupId is { } groupId && groupNames.TryGetValue(groupId, out var name)
                ? name
                : null;

            // The projection starts at the series' *current* cycle, so every cycle it yields is one the
            // series has not advanced past yet — that is exactly the live set. Completing or skipping a
            // cycle moves the current cycle forward, and the retired cycle stops being projected.
            foreach (var occurrence in _recurrence.ProjectOccurrencesInWindow(task.Recurrence, task.When, projectionEnd))
            {
                var deliveryTime = ReminderTimeCalculator.Calculate(occurrence, task.Reminder);
                if (deliveryTime is null)
                    continue;

                // The occurrence's stable id — the same one CompleteAsync derives — so a completed cycle's
                // tag leaves the expected set once the series advances past it.
                var occurrenceId = RecurrenceOccurrenceId.From(task.Id, occurrence.Date!.Value.Utc);
                var tag = NotificationScheduler.TagForOccurrence(occurrenceId);

                // Live regardless of the window: a cycle whose toast already fired is no longer schedulable
                // but is still unresolved, so its notification-center entry must be kept.
                liveTags.Add(tag);

                if (deliveryTime.Value <= now || deliveryTime.Value > windowEnd)
                    continue;

                // Pre-reminders show the actual occurrence time on the toast's second line; an at-time
                // reminder fires at the occurrence itself, so there is nothing extra to display.
                DateTimeOffset? scheduledTime = task.Reminder != ReminderTiming.AtTime
                    ? occurrence.Date!.Value.ToLocal()
                    : null;

                expected.Add(new ScheduledNotification(
                    deliveryTime.Value,
                    tag,
                    task.Title,
                    groupName,
                    scheduledTime,
                    task.Reminder,
                    // Carry the occurrenceId so a headless complete finishes only this cycle, not the series.
                    new ToastActivationPayload(ToastAction.Complete, task.Id, occurrenceId).ToArguments()));
            }
        }

        return new RecurringNotificationSet(expected, liveTags);
    }
}
