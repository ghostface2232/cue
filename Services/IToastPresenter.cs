using Cue.Domain;

namespace Cue.Services;

/// <summary>
/// App-layer boundary for Windows toast delivery. No Toolkit or WinRT notification type crosses this
/// interface, keeping the maintenance-mode package replaceable and out of Domain, Storage, and ViewModels.
/// </summary>
public interface IToastPresenter
{
    void Schedule(ToastScheduleRequest request);

    void CancelScheduled(string tag);

    /// <summary>
    /// Removes a delivered or snoozed toast from the notification center by tag. This is separate from
    /// <see cref="CancelScheduled"/> which only removes pending scheduled toasts from the OS schedule
    /// queue. A system-snoozed toast lives in the notification center, not the schedule queue, so
    /// cancelling a snoozed toast requires clearing its history entry.
    /// </summary>
    void RemoveFromHistory(string tag);

    IReadOnlyList<string> GetScheduledTags();

    /// <summary>
    /// Tags of toasts this app has already delivered that are still sitting in the notification center
    /// (Action Center). These are gone from the schedule queue that <see cref="GetScheduledTags"/>
    /// reports, so reconciling delivered-toast cleanup — revoking a fired toast whose task was since
    /// completed or deleted — requires this separate view of history.
    /// </summary>
    IReadOnlyList<string> GetHistoryTags();

    void Show(string title, string body, string? activationArguments = null);
}

/// <summary>All data the adapter needs to construct a single scheduled toast notification.</summary>
/// <param name="DeliveryTime">When the OS should show the toast.</param>
/// <param name="Tag">Unique tag for diff/cancel.</param>
/// <param name="Title">Task title — the first line of the toast.</param>
/// <param name="GroupName">Owning task group name, shown as the second line when non-null.</param>
/// <param name="ScheduledTime">
/// The task's actual wall-clock time (for display in pre-reminders). Null when the delivery is at-time.
/// </param>
/// <param name="Reminder">The reminder timing that produced this delivery.</param>
/// <param name="CompletionArguments">The <c>action=complete&amp;taskId=...</c> activation payload.</param>
public sealed record ToastScheduleRequest(
    DateTimeOffset DeliveryTime,
    string Tag,
    string Title,
    string? GroupName,
    DateTimeOffset? ScheduledTime,
    ReminderTiming Reminder,
    string CompletionArguments);

/// <summary>Toolkit-independent source of toast activation payloads.</summary>
internal interface IToastActivationSource
{
    bool WasCurrentProcessToastActivated { get; }

    event EventHandler<ToastActivationRequest> Activated;
}

internal sealed class ToastActivationRequest(string arguments) : EventArgs
{
    public string Arguments { get; } = arguments;
}
