using System.Diagnostics;
using Cue.Domain;
using Cue.Storage;

namespace Cue.Services;

/// <summary>One expected OS-scheduled toast derived from a task record.</summary>
public sealed record ScheduledNotification(
    DateTimeOffset DeliveryTime,
    string Tag,
    string Title,
    string? GroupName,
    DateTimeOffset? ScheduledTime,
    ReminderTiming Reminder,
    string CompletionArguments);

/// <summary>
/// Extension seam for recurring occurrences. The foundation scheduler excludes recurring tasks; the
/// next step can contribute occurrence notifications here without changing one-off reconciliation.
/// </summary>
public interface IRecurringNotificationSource
{
    Task<IReadOnlyList<ScheduledNotification>> GetExpectedAsync(
        DateTimeOffset now,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reconciles task-record truth with the OS scheduled-toast derivative over a rolling 14-day window.
/// Save bursts are debounced, and a periodic pass backs up event delivery rather than trusting one signal.
/// </summary>
public sealed class NotificationScheduler : IAsyncDisposable
{
    public static readonly TimeSpan RollingWindow = TimeSpan.FromDays(14);
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan DefaultPeriodicInterval = TimeSpan.FromMinutes(15);

    private const string TaskTagPrefix = "task-";
    private const string OccurrenceTagPrefix = "occurrence-";

    private readonly ITaskStore _store;
    private readonly ITaskStoreChangeSource _changes;
    private readonly IToastPresenter _toasts;
    private readonly INotificationPreferences _preferences;
    private readonly TimeProvider _clock;
    private readonly IReadOnlyList<IRecurringNotificationSource> _recurringSources;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _periodicInterval;
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _debounceGate = new();

    private IReadOnlyDictionary<string, ScheduledNotification> _lastExpected =
        new Dictionary<string, ScheduledNotification>(StringComparer.Ordinal);
    private CancellationTokenSource? _debounceCancellation;
    private Task? _periodicTask;
    private int _started;

    public NotificationScheduler(
        ITaskStore store,
        ITaskStoreChangeSource changes,
        IToastPresenter toasts,
        INotificationPreferences preferences,
        TimeProvider clock,
        IEnumerable<IRecurringNotificationSource>? recurringSources = null,
        TimeSpan? debounce = null,
        TimeSpan? periodicInterval = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _changes = changes ?? throw new ArgumentNullException(nameof(changes));
        _toasts = toasts ?? throw new ArgumentNullException(nameof(toasts));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _recurringSources = recurringSources?.ToArray() ?? [];
        _debounce = debounce ?? DefaultDebounce;
        _periodicInterval = periodicInterval ?? DefaultPeriodicInterval;
    }

    /// <summary>Subscribes to change triggers and performs the required startup reconciliation once.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        _changes.Changed += OnReconcileTrigger;
        _preferences.NotificationsEnabledChanged += OnReconcileTrigger;

        // OS exposes tags but not enough metadata to compare delivery times. Refresh matching startup
        // entries once so the record's current time always wins across app restarts.
        try
        {
            await ReconcileAsync(refreshExisting: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Toast registration is a derived-cache concern; it must not prevent the source-of-truth app
            // from opening. The periodic pass will retry after a transient OS notification failure.
            Debug.WriteLine($"[Cue] Startup notification reconcile failed: {exception}");
        }

        if (_periodicInterval > TimeSpan.Zero && _periodicInterval != Timeout.InfiniteTimeSpan)
            _periodicTask = RunPeriodicAsync();
    }

    /// <summary>Builds the expected 14-day set exclusively from current task records.</summary>
    public async Task<IReadOnlyDictionary<string, ScheduledNotification>> BuildExpectedAsync(
        CancellationToken cancellationToken = default)
    {
        var expected = new Dictionary<string, ScheduledNotification>(StringComparer.Ordinal);
        if (!_preferences.NotificationsEnabled)
            return expected;

        var now = _clock.GetUtcNow();
        var windowEnd = now + RollingWindow;
        var tasks = await _store.GetAllAsync<TaskItem>(cancellationToken).ConfigureAwait(false);

        // Pre-load groups for name lookup (only non-deleted ones).
        var groups = await _store.GetAllAsync<TaskGroup>(cancellationToken).ConfigureAwait(false);
        var groupNames = groups
            .Where(g => !g.IsDeleted)
            .ToDictionary(g => g.Id, g => g.Name);

        foreach (var task in tasks)
        {
            if (task.IsDeleted || task.IsCompleted || task.Recurrence is not null ||
                task.Reminder == ReminderTiming.None)
            {
                continue;
            }

            var deliveryTime = ReminderTimeCalculator.Calculate(task.When, task.Reminder);
            if (deliveryTime is null || deliveryTime.Value <= now || deliveryTime.Value > windowEnd)
                continue;

            var tag = TagForTask(task.Id);
            string? groupName = task.TaskGroupId is { } gid && groupNames.TryGetValue(gid, out var name)
                ? name
                : null;

            // For pre-reminders, include the actual scheduled wall-clock time so the toast can show it.
            // AtTime reminders fire at the task time itself, so there's nothing extra to display.
            DateTimeOffset? scheduledTime = task.Reminder != ReminderTiming.AtTime
                ? task.When.Date!.Value.ToLocal()
                : null;

            expected[tag] = new ScheduledNotification(
                deliveryTime.Value,
                tag,
                task.Title,
                groupName,
                scheduledTime,
                task.Reminder,
                $"action=complete&taskId={task.Id:D}");
        }

        foreach (var source in _recurringSources)
        {
            foreach (var notification in await source.GetExpectedAsync(now, windowEnd, cancellationToken)
                         .ConfigureAwait(false))
            {
                expected[notification.Tag] = notification;
            }
        }

        return expected;
    }

    /// <summary>Diffs expected records against OS tags and applies only required changes.</summary>
    public async Task ReconcileAsync(
        bool refreshExisting = false,
        CancellationToken cancellationToken = default)
    {
        await _reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var expected = await BuildExpectedAsync(cancellationToken).ConfigureAwait(false);
            var actual = _toasts.GetScheduledTags()
                .Where(IsManagedTag)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var tag in actual.Where(tag => !expected.ContainsKey(tag)).ToArray())
            {
                _toasts.CancelScheduled(tag);
                // Also clear from notification center in case the toast already fired and is snoozed.
                // A system-snoozed toast lives in the history, not the schedule queue, so both calls
                // are needed to fully revoke a stale notification.
                _toasts.RemoveFromHistory(tag);
            }

            foreach (var (tag, notification) in expected)
            {
                var exists = actual.Contains(tag);
                var changedSinceLastPass = _lastExpected.TryGetValue(tag, out var previous) &&
                                           previous != notification;

                if (exists && (refreshExisting || changedSinceLastPass))
                {
                    _toasts.CancelScheduled(tag);
                    _toasts.RemoveFromHistory(tag);
                    exists = false;
                }

                if (!exists)
                {
                    _toasts.Schedule(new ToastScheduleRequest(
                        notification.DeliveryTime,
                        notification.Tag,
                        notification.Title,
                        notification.GroupName,
                        notification.ScheduledTime,
                        notification.Reminder,
                        notification.CompletionArguments));
                }
            }

            _lastExpected = new Dictionary<string, ScheduledNotification>(expected, StringComparer.Ordinal);
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    public static string TagForTask(Guid taskId) => $"{TaskTagPrefix}{taskId:N}";

    public async ValueTask DisposeAsync()
    {
        _changes.Changed -= OnReconcileTrigger;
        _preferences.NotificationsEnabledChanged -= OnReconcileTrigger;
        _shutdown.Cancel();

        CancellationTokenSource? debounce;
        lock (_debounceGate)
        {
            debounce = _debounceCancellation;
            _debounceCancellation = null;
        }
        debounce?.Cancel();
        debounce?.Dispose();

        if (_periodicTask is not null)
        {
            try { await _periodicTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        _shutdown.Dispose();
        _reconcileGate.Dispose();
    }

    private static bool IsManagedTag(string tag)
        => tag.StartsWith(TaskTagPrefix, StringComparison.Ordinal) ||
           tag.StartsWith(OccurrenceTagPrefix, StringComparison.Ordinal);

    private void OnReconcileTrigger(object? sender, EventArgs args)
    {
        CancellationTokenSource cancellation;
        lock (_debounceGate)
        {
            _debounceCancellation?.Cancel();
            _debounceCancellation?.Dispose();
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            _debounceCancellation = cancellation;
        }
        _ = ReconcileAfterDebounceAsync(cancellation);
    }

    private async Task ReconcileAfterDebounceAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(_debounce, _clock, cancellation.Token).ConfigureAwait(false);
            await ReconcileAsync(cancellationToken: cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[Cue] Notification reconcile failed: {exception}");
        }
        finally
        {
            lock (_debounceGate)
            {
                if (ReferenceEquals(_debounceCancellation, cancellation))
                    _debounceCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private async Task RunPeriodicAsync()
    {
        using var timer = new PeriodicTimer(_periodicInterval, _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try { await ReconcileAsync(cancellationToken: _shutdown.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return; }
                catch (Exception exception)
                {
                    Debug.WriteLine($"[Cue] Periodic notification reconcile failed: {exception}");
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }
}
