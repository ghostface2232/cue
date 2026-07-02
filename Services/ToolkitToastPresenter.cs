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
    private readonly object _activationGate = new();
    private readonly Queue<ToastActivationRequest> _bufferedActivations = new();
    private EventHandler<ToastActivationRequest>? _activated;

    public ToolkitToastPresenter()
        => ToastNotificationManagerCompat.OnActivated += OnToolkitActivated;

    public bool WasCurrentProcessToastActivated
        => ToastNotificationManagerCompat.WasCurrentProcessToastActivated();

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

    public void Schedule(
        DateTimeOffset deliveryTime,
        string tag,
        string title,
        string body,
        string completionArguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        if (!ToastActivationPayload.TryParse(completionArguments, out var payload) ||
            payload!.Action != ToastAction.Complete)
        {
            throw new ArgumentException("A valid complete action payload is required.", nameof(completionArguments));
        }

        var content = new ToastContentBuilder()
            .AddToastActivationInfo(payload.AsOpen().ToArguments(), ToastActivationType.Foreground)
            .AddText(title)
            .AddText(body)
            // Desktop ignores background activation as a true background task and starts/uses the EXE.
            // App routing deliberately treats this action as headless even though the process is activated.
            .AddButton("완료", ToastActivationType.Background, completionArguments)
            .GetToastContent();

        var notification = new ScheduledToastNotification(content.GetXml(), deliveryTime)
        {
            Tag = tag,
        };
        ToastNotificationManagerCompat.CreateToastNotifier().AddToSchedule(notification);
    }

    public void CancelScheduled(string tag)
    {
        var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
        foreach (var notification in notifier.GetScheduledToastNotifications()
                     .Where(item => string.Equals(item.Tag, tag, StringComparison.Ordinal)))
        {
            notifier.RemoveFromSchedule(notification);
        }
    }

    public IReadOnlyList<string> GetScheduledTags()
        => ToastNotificationManagerCompat.CreateToastNotifier()
            .GetScheduledToastNotifications()
            .Select(notification => notification.Tag)
            .Where(tag => !string.IsNullOrEmpty(tag))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    public void Show(string title, string body, string? activationArguments = null)
    {
        var builder = new ToastContentBuilder().AddText(title).AddText(body);
        if (!string.IsNullOrWhiteSpace(activationArguments))
            builder.AddToastActivationInfo(activationArguments, ToastActivationType.Foreground);

        var notification = new ToastNotification(builder.GetToastContent().GetXml());
        ToastNotificationManagerCompat.CreateToastNotifier().Show(notification);
    }

    public void Dispose()
        => ToastNotificationManagerCompat.OnActivated -= OnToolkitActivated;

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
