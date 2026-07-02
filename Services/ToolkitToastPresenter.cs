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
        : this(RuntimeIdentity.IsPackaged)
    {
    }

    /// <summary>
    /// Channel identity is injected so the toast <b>registration/activation wiring</b> — the one real
    /// per-channel behavior branch in Cue (AGENTS.md distribution invariants) — is driven by the single
    /// runtime identity helper rather than by <c>#if</c>. The two branches below still call the same WCT
    /// compat APIs because the library itself forks internally on the same identity check; splitting them
    /// here documents each channel's contract, keeps the diagnostics accurate, and gives the packaged
    /// manifest requirements a home. Everything above this seam — the adapter interface, the reconcile
    /// loop, and the Tag scheme — is identical across channels.
    /// </summary>
    internal ToolkitToastPresenter(bool isPackaged)
    {
        _available = isPackaged ? TryRegisterPackaged() : TryRegisterUnpackaged();
    }

    /// <summary>
    /// Unpackaged (GitHub channel) registration. The WCT compat library auto-registers a Win32 COM
    /// activator plus the app's AUMID in the per-user registry the first time its APIs are touched — no
    /// manifest and (in 7.1.x) no Start-menu shortcut are required. Subscribing to OnActivated performs
    /// that registration; the CreateToastNotifier probe confirms it took. The Inno Setup uninstaller
    /// clears those registry keys (equivalent to <c>ToastNotificationManagerCompat.Uninstall()</c>), per
    /// the AGENTS.md gotcha — and because the packaged channel never enters this path, it leaves nothing
    /// to clean up there.
    /// </summary>
    private bool TryRegisterUnpackaged()
        => TrySubscribeAndProbe("registry COM-activator registration failed");

    /// <summary>
    /// Packaged (Microsoft Store / MSIX channel) registration. Here WCT does NOT touch the registry: it
    /// reads the activator identity straight from the running package's <c>AppxManifest.xml</c>
    /// (Microsoft.Toolkit.Uwp.Notifications 7.1.3, <c>ManifestHelper.GetClsidFromPackageManifest</c>).
    /// For that lookup to resolve, the manifest merged at packaging time (Step 5) MUST declare, on the
    /// primary <c>&lt;Application&gt;</c>, with <c>desktop</c>/<c>com</c> added to IgnorableNamespaces:
    /// <code>
    ///   &lt;!-- xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"
    ///        xmlns:com="http://schemas.microsoft.com/appx/manifest/com/windows10" --&gt;
    ///   &lt;Extensions&gt;
    ///     &lt;desktop:Extension Category="windows.toastNotificationActivation"&gt;
    ///       &lt;desktop:ToastNotificationActivation ToastActivatorCLSID="{GUID}" /&gt;
    ///     &lt;/desktop:Extension&gt;
    ///     &lt;com:Extension Category="windows.comServer"&gt;
    ///       &lt;com:ComServer&gt;
    ///         &lt;com:ExeServer Executable="Cue.exe" Arguments="-ToastActivated" DisplayName="Cue Toast Activator"&gt;
    ///           &lt;com:Class Id="{GUID}" /&gt;
    ///         &lt;/com:ExeServer&gt;
    ///       &lt;/com:ComServer&gt;
    ///     &lt;/com:Extension&gt;
    ///   &lt;/Extensions&gt;
    /// </code>
    /// Exact conditions the library enforces (else it throws on first use):
    /// <list type="bullet">
    ///   <item>the <c>ToastActivatorCLSID</c> GUID and the <c>com:Class</c> <c>Id</c> must be identical;</item>
    ///   <item><c>com:ExeServer/@Executable</c> must resolve to the actually-running exe in the package;</item>
    ///   <item><c>com:ExeServer/@Arguments</c> must be exactly <c>-ToastActivated</c> — this is the WCT
    ///     compat contract, NOT the Windows App SDK's <c>----AppNotificationActivated:</c> value;</item>
    ///   <item>the CLSID is picked once by us and lives only in the manifest; the library derives it from
    ///     there and writes nothing at runtime.</item>
    /// </list>
    /// The library defers the exception to the first CreateToastNotifier, so a packaged build missing
    /// these fragments (e.g. <c>winapp run</c>, which grants a debug identity but ships no toast manifest)
    /// degrades to unavailable here instead of crashing — toasts are simply off in that dev scenario.
    /// </summary>
    private bool TryRegisterPackaged()
        => TrySubscribeAndProbe("manifest is missing the toastNotificationActivation / comServer extension");

    /// <summary>
    /// Shared wiring for both channels: subscribe to activations, then probe the notifier so a broken
    /// registration surfaces now (and every toast op then no-ops via the <c>_available</c> guard) rather
    /// than at the first schedule. Only the diagnostic hint differs per channel.
    /// </summary>
    private bool TrySubscribeAndProbe(string failureHint)
    {
        try
        {
            ToastNotificationManagerCompat.OnActivated += OnToolkitActivated;
            _ = ToastNotificationManagerCompat.CreateToastNotifier();
            return true;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[Cue] Toast notifications unavailable ({failureHint}); scheduling disabled: {exception.Message}");
            try { ToastNotificationManagerCompat.OnActivated -= OnToolkitActivated; }
            catch { /* best-effort unsubscribe; registration never completed */ }
            return false;
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

    public IReadOnlyList<string> GetHistoryTags()
    {
        if (!_available) return [];

        try
        {
            return ToastNotificationManagerCompat.History.GetHistory()
                .Select(notification => notification.Tag)
                .Where(tag => !string.IsNullOrEmpty(tag))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[Cue] Failed to read toast history: {exception.Message}");
            return [];
        }
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
    ///   <item>Action 1: 할 일 완료 button (background activation → complete action)</item>
    ///   <item>Action 2: System snooze with selection input (1분, 10분, 30분, 1시간)</item>
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

        // Action 1: 할 일 완료 button. Spelled out (not bare "완료") so it reads as completing the task
        // rather than a generic dismiss/confirm that just closes the toast.
        builder.AddButton("할 일 완료", ToastActivationType.Background, request.CompletionArguments);

        // Action 2: System snooze (selection input + system snooze action).
        AddSnoozeAction(builder);

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
    /// entirely, so this works even when the app is not running. Choices are fixed durations —
    /// 1분, 10분, 30분, 1시간. They must be interval offsets, not absolute targets: the snooze minute
    /// value is baked into the static toast XML at schedule time, and the OS applies it relative to
    /// whenever the toast is snoozed. An earlier "오늘 저녁 (18:00)" choice violated that — it encoded
    /// the gap from the original delivery to 18:00, so snoozing 10분 first and then picking it re-added
    /// the full original gap and overshot 18:00. Fixed durations have no anchor to drift from.
    /// </summary>
    private static void AddSnoozeAction(ToastContentBuilder builder)
    {
        var selectionBox = new ToastSelectionBox(SnoozeSelectionId)
        {
            DefaultSelectionBoxItemId = "10",
            Items =
            {
                new ToastSelectionBoxItem("1", "1분"),
                new ToastSelectionBoxItem("10", "10분"),
                new ToastSelectionBoxItem("30", "30분"),
                new ToastSelectionBoxItem("60", "1시간"),
            },
        };

        builder.AddToastInput(selectionBox);

        // ToastButtonSnooze produces activationType="system" arguments="snooze" automatically.
        // Linking it to the selection box via SelectionBoxId makes the OS read the user's chosen
        // minute value for the snooze interval.
        builder.AddButton(new ToastButtonSnooze("다시 알림")
        {
            SelectionBoxId = SnoozeSelectionId,
        });
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
