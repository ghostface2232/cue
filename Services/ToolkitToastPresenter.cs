using System.Diagnostics;
using Cue.Domain;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;

namespace Cue.Services;

/// <summary>
/// The only adapter that touches Microsoft.Toolkit.Uwp.Notifications. The package is in maintenance
/// mode, so the rest of Cue depends only on <see cref="IToastPresenter"/> and
/// <see cref="IToastActivationSource"/> and can replace this implementation later.
/// </summary>
internal sealed class ToolkitToastPresenter : IToastPresenter, IToastActivationSource, IDisposable
{
    /// <summary>The fixed selection-box id that links to the system snooze action.</summary>
    private const string SnoozeSelectionId = "snoozeTime";

    private readonly object _activationGate = new();
    private readonly Queue<ToastActivationRequest> _bufferedActivations = new();
    private readonly bool _available;
    private EventHandler<ToastActivationRequest>? _activated;

    public ToolkitToastPresenter()
    {
        // The Compat library's OnActivated subscription triggers an immediate COM activator
        // registration that reads the app manifest for a toastNotificationActivation CLSID.
        // In an unpackaged dev-loop build (no manifest / no registered AUMID) this throws
        // InvalidOperationException. Catch it here so the rest of the app starts normally;
        // toast scheduling will gracefully no-op via the _available guard.
        try
        {
            ToastNotificationManagerCompat.OnActivated += OnToolkitActivated;
            _available = true;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[Cue] Toast notification registration unavailable: {exception.Message}");
            _available = false;
        }
    }

    public bool WasCurrentProcessToastActivated
    {
        get
        {
            if (!_available) return false;
            try { return ToastNotificationManagerCompat.WasCurrentProcessToastActivated(); }
            catch { return false; }
        }
    }

    public event EventHandler<ToastActivationRequest> Activated
    {
        add
        {
            List<ToastActivationRequest>? buffered = null;
            lock (_activationGate)
            {
                _activated += value;
                if (_bufferedActivations.Count > 0)
                {
                    buffered = new List<ToastActivationRequest>(_bufferedActivations.Count);
                    while (_bufferedActivations.TryDequeue(out var request))
                        buffered.Add(request);
                }
            }

            if (buffered is not null)
            {
                foreach (var request in buffered)
                    value(this, request);
            }
        }
        remove
        {
            lock (_activationGate)
                _activated -= value;
        }
    }

    public void Schedule(ToastScheduleRequest request)
    {
        if (!_available) return;

        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Tag);
        if (!ToastActivationPayload.TryParse(request.CompletionArguments, out var payload) ||
            payload!.Action != ToastAction.Complete)
        {
            throw new ArgumentException("A valid complete action payload is required.",
                nameof(request));
        }

        var content = BuildToastContent(request, payload);
        var notification = new ScheduledToastNotification(content.GetXml(), request.DeliveryTime)
        {
            Tag = request.Tag,
        };
        ToastNotificationManagerCompat.CreateToastNotifier().AddToSchedule(notification);
    }

    public void CancelScheduled(string tag)
    {
        if (!_available) return;

        var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
        foreach (var notification in notifier.GetScheduledToastNotifications()
                     .Where(item => string.Equals(item.Tag, tag, StringComparison.Ordinal)))
        {
            notifier.RemoveFromSchedule(notification);
        }
    }

    public void RemoveFromHistory(string tag)
    {
        if (!_available) return;

        try { ToastNotificationManagerCompat.History.Remove(tag); }
        catch (Exception exception)
        {
            Debug.WriteLine($"[Cue] Failed to remove toast from history: {exception.Message}");
        }
    }

    public IReadOnlyList<string> GetScheduledTags()
    {
        if (!_available) return [];

        return ToastNotificationManagerCompat.CreateToastNotifier()
            .GetScheduledToastNotifications()
            .Select(notification => notification.Tag)
            .Where(tag => !string.IsNullOrEmpty(tag))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public void Show(string title, string body, string? activationArguments = null)
    {
        if (!_available) return;

        var builder = new ToastContentBuilder().AddText(title).AddText(body);
        if (!string.IsNullOrWhiteSpace(activationArguments))
            builder.AddToastActivationInfo(activationArguments, ToastActivationType.Foreground);

        var notification = new ToastNotification(builder.GetToastContent().GetXml());
        ToastNotificationManagerCompat.CreateToastNotifier().Show(notification);
    }

    public void Dispose()
    {
        if (_available)
            ToastNotificationManagerCompat.OnActivated -= OnToolkitActivated;
    }

    // ── Toast content construction ──────────────────────────────────────────

    /// <summary>
    /// Builds the toast XML with:
    /// <list type="bullet">
    ///   <item>Line 1: task title</item>
    ///   <item>Line 2: group name (when present) + scheduled time for pre-reminders</item>
    ///   <item>Action 1: 완료 button (background activation → complete action)</item>
    ///   <item>Action 2: System snooze with selection input (10분, 1시간, 오늘 저녁)</item>
    /// </list>
    /// </summary>
    private static ToastContent BuildToastContent(ToastScheduleRequest request, ToastActivationPayload payload)
    {
        var builder = new ToastContentBuilder()
            // Body click → open app and navigate to the task.
            .AddToastActivationInfo(payload.AsOpen().ToArguments(), ToastActivationType.Foreground)
            .SetToastScenario(ToastScenario.Reminder)
            .AddText(request.Title);

        // Second line: group name and/or scheduled time for pre-reminders.
        var secondLine = BuildSecondLine(request);
        if (secondLine is not null)
            builder.AddText(secondLine);

        // Action 1: 완료 button.
        builder.AddButton("완료", ToastActivationType.Background, request.CompletionArguments);

        // Action 2: System snooze (selection input + system snooze action).
        AddSnoozeAction(builder, request);

        return builder.GetToastContent();
    }

    /// <summary>
    /// Builds the second line of the toast body. Returns null when there's nothing to show.
    /// </summary>
    /// <remarks>
    /// For pre-reminders (10분 전 / 1시간 전 / 하루 전), the second line shows the actual scheduled time
    /// so the user knows when the task is due, e.g. "오후 3:00 예정" or "프로젝트 · 오후 3:00 예정".
    /// For at-time reminders, only the group name is shown (if any).
    /// </remarks>
    private static string? BuildSecondLine(ToastScheduleRequest request)
    {
        var parts = new List<string>(2);

        if (!string.IsNullOrWhiteSpace(request.GroupName))
            parts.Add(request.GroupName);

        if (request.Reminder != ReminderTiming.AtTime && request.ScheduledTime is { } scheduledTime)
            parts.Add(FormatScheduledTime(scheduledTime) + " 예정");

        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    /// <summary>Formats a wall-clock time as "오전/오후 H:mm" using Korean conventions.</summary>
    private static string FormatScheduledTime(DateTimeOffset time)
    {
        var hour = time.Hour;
        var minute = time.Minute;
        var period = hour < 12 ? "오전" : "오후";
        var displayHour = hour % 12;
        if (displayHour == 0) displayHour = 12;
        return $"{period} {displayHour}:{minute:D2}";
    }

    /// <summary>
    /// Adds the system snooze selection input and snooze button. The OS handles the re-scheduling
    /// entirely, so this works even when the app is not running. Choices: 10분, 1시간, and
    /// "오늘 저녁" (minutes from delivery time to 18:00 on the delivery day, included only when the
    /// delivery is before 18:00).
    /// </summary>
    private static void AddSnoozeAction(ToastContentBuilder builder, ToastScheduleRequest request)
    {
        // Compute the "오늘 저녁 (18:00)" snooze value: minutes from delivery time to 18:00 local.
        int? eveningMinutes = ComputeEveningSnoozeMinutes(request.DeliveryTime);

        var selectionBox = new ToastSelectionBox(SnoozeSelectionId)
        {
            DefaultSelectionBoxItemId = "10",
            Items =
            {
                new ToastSelectionBoxItem("10", "10분"),
                new ToastSelectionBoxItem("60", "1시간"),
            },
        };

        if (eveningMinutes is > 0)
            selectionBox.Items.Add(new ToastSelectionBoxItem(
                eveningMinutes.Value.ToString(), "오늘 저녁 (18:00)"));

        builder.AddToastInput(selectionBox);

        // ToastButtonSnooze produces activationType="system" arguments="snooze" automatically.
        // Linking it to the selection box via SelectionBoxId makes the OS read the user's chosen
        // minute value for the snooze interval.
        builder.AddButton(new ToastButtonSnooze("다시 알림")
        {
            SelectionBoxId = SnoozeSelectionId,
        });
    }

    /// <summary>
    /// Computes the number of minutes from the delivery time to 18:00 on the same local day. Returns
    /// null when the delivery is at or after 18:00 (the option would be nonsensical) or when the
    /// resulting value is ≤ 0 or ≤ 10 (too close to be useful — the 10-minute option already covers it).
    /// </summary>
    private static int? ComputeEveningSnoozeMinutes(DateTimeOffset deliveryTime)
    {
        // Use the delivery time's own offset to compute the local 18:00 on the same day.
        var localDelivery = deliveryTime;
        var evening = new DateTimeOffset(
            localDelivery.Year, localDelivery.Month, localDelivery.Day,
            18, 0, 0, localDelivery.Offset);
        var delta = (int)(evening - localDelivery).TotalMinutes;
        // Include only when there's a meaningful gap (more than 10 minutes, so it's distinct from the
        // fixed 10-minute choice).
        return delta > 10 ? delta : null;
    }

    // ── Activation routing ──────────────────────────────────────────────────

    private void OnToolkitActivated(ToastNotificationActivatedEventArgsCompat args)
    {
        var request = new ToastActivationRequest(args.Argument);
        EventHandler<ToastActivationRequest>? listeners;
        lock (_activationGate)
        {
            listeners = _activated;
            if (listeners is null)
            {
                _bufferedActivations.Enqueue(request);
                return;
            }
        }

        listeners(this, request);
    }
}
