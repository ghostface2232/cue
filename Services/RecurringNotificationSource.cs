using Cue.Domain;
using Cue.Storage;
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
/// </remarks>
internal sealed class RecurringNotificationSource : IRecurringNotificationSource
{
    // The largest pre-reminder lead (OneDayBefore). An occurrence whose instant sits up to this far beyond
    // the window can still deliver inside it (delivery = instant − lead), so project that much past the
    // window and let the per-occurrence delivery check decide — matching the one-off path's window test.
    private static readonly TimeSpan MaxReminderLead = TimeSpan.FromHours(24);

    private readonly ITaskStore _store;
    private readonly IRecurringTaskService _recurrence;

    public RecurringNotificationSource(ITaskStore store, IRecurringTaskService recurrence)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _recurrence = recurrence ?? throw new ArgumentNullException(nameof(recurrence));
    }

    public async Task<IReadOnlyList<ScheduledNotification>> GetExpectedAsync(
        DateTimeOffset now, DateTimeOffset windowEnd, CancellationToken cancellationToken = default)
    {
        var tasks = await _store.GetAllAsync<TaskItem>(cancellationToken).ConfigureAwait(false);

        // Group names for the toast's second line (non-deleted only), same as the one-off path.
        var groups = await _store.GetAllAsync<TaskGroup>(cancellationToken).ConfigureAwait(false);
        var groupNames = groups
            .Where(group => !group.IsDeleted)
            .ToDictionary(group => group.Id, group => group.Name);

        var projectionEnd = windowEnd + MaxReminderLead;
        var expected = new List<ScheduledNotification>();

        foreach (var task in tasks)
        {
            // Only recurring series belong here — one-off tasks are the base scheduler's job. All-day series
            // carry no individual toast (the opt-in daily briefing covers them); None disables the series.
            if (task.IsDeleted || task.IsCompleted || task.Recurrence is null ||
                task.Reminder == ReminderTiming.None || task.When.IsAllDay)
            {
                continue;
            }

            string? groupName = task.TaskGroupId is { } groupId && groupNames.TryGetValue(groupId, out var name)
                ? name
                : null;

            foreach (var occurrence in _recurrence.ProjectOccurrencesInWindow(task.Recurrence, task.When, projectionEnd))
            {
                var deliveryTime = ReminderTimeCalculator.Calculate(occurrence, task.Reminder);
                if (deliveryTime is null || deliveryTime.Value <= now || deliveryTime.Value > windowEnd)
                    continue;

                // The occurrence's stable id — the same one CompleteAsync derives — so a completed cycle's
                // tag leaves the expected set once the series advances past it.
                var occurrenceId = RecurrenceOccurrenceId.From(task.Id, occurrence.Date!.Value.Utc);

                // Pre-reminders show the actual occurrence time on the toast's second line; an at-time
                // reminder fires at the occurrence itself, so there is nothing extra to display.
                DateTimeOffset? scheduledTime = task.Reminder != ReminderTiming.AtTime
                    ? occurrence.Date!.Value.ToLocal()
                    : null;

                expected.Add(new ScheduledNotification(
                    deliveryTime.Value,
                    NotificationScheduler.TagForOccurrence(occurrenceId),
                    task.Title,
                    groupName,
                    scheduledTime,
                    task.Reminder,
                    // Carry the occurrenceId so a headless complete finishes only this cycle, not the series.
                    new ToastActivationPayload(ToastAction.Complete, task.Id, occurrenceId).ToArguments()));
            }
        }

        return expected;
    }
}
