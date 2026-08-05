using System.Diagnostics;
using Cue.Domain;
using Cue.Storage;
using Cue.Storage.Index;

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
/// <remarks>
/// The expected set is built from the SQLite index, not the record files. A reconcile pass runs on every
/// save (debounced) and every 15 minutes, so reading the folder here meant re-deserializing every task on
/// each keystroke-driven autosave — the index answers the same question with one indexed query. This is
/// the ordinary list-read rule (invariant: list reads come from the index) applied to a list read that had
/// been written against the store. Index staleness is already tolerated by design: the startup pass
/// rebuilds the index first and then reconciles, and the periodic pass re-runs regardless.
/// </remarks>
public sealed class NotificationScheduler : IAsyncDisposable
{
    public static readonly TimeSpan RollingWindow = TimeSpan.FromDays(14);
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan DefaultPeriodicInterval = TimeSpan.FromMinutes(15);

    // How far past the rolling window the candidate day-range reaches. A pre-reminder leads its task by at
    // most a day (OneDayBefore), and a task's when_date is a calendar day in its *own* zone, which can sit
    // a day either side of the UTC bounds. One day of slack on each end covers both; the per-candidate
    // delivery test still decides membership, so the slack only widens what is read, never what is kept.
    private static readonly TimeSpan CandidateRangeSlack = TimeSpan.FromDays(1);

    private const string TaskTagPrefix = "task-";
    private const string OccurrenceTagPrefix = "occurrence-";

    private readonly ITaskIndex _index;
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
        ITaskIndex index,
        ITaskStoreChangeSource changes,
        IToastPresenter toasts,
        INotificationPreferences preferences,
        TimeProvider clock,
        IEnumerable<IRecurringNotificationSource>? recurringSources = null,
        TimeSpan? debounce = null,
        TimeSpan? periodicInterval = null)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
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

    /// <summary>Builds the expected 14-day set exclusively from current task records (read through the index).</summary>
    public async Task<IReadOnlyDictionary<string, ScheduledNotification>> BuildExpectedAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_preferences.NotificationsEnabled)
            return new Dictionary<string, ScheduledNotification>(StringComparer.Ordinal);

        var candidates = await ReadCandidatesAsync(cancellationToken).ConfigureAwait(false);
        return await BuildExpectedCoreAsync(candidates, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Queries the index for every task that could plausibly deliver inside the rolling window.
    /// The day range is intentionally one day wider than the window at each end (see
    /// <see cref="CandidateRangeSlack"/>); membership is decided per candidate below.</summary>
    private Task<IReadOnlyList<ReminderCandidate>> ReadCandidatesAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        return _index.GetReminderCandidatesAsync(
            DateOnly.FromDateTime((now - CandidateRangeSlack).UtcDateTime),
            DateOnly.FromDateTime((now + RollingWindow + CandidateRangeSlack).UtcDateTime),
            cancellationToken);
    }

    /// <summary>Derives the expected set from an already-queried candidate list, so
    /// <see cref="ReconcileAsync"/> can hit the index once and reuse the result.</summary>
    private async Task<IReadOnlyDictionary<string, ScheduledNotification>> BuildExpectedCoreAsync(
        IReadOnlyList<ReminderCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var expected = new Dictionary<string, ScheduledNotification>(StringComparer.Ordinal);

        var now = _clock.GetUtcNow();
        var windowEnd = now + RollingWindow;

        foreach (var candidate in candidates)
        {
            // The exact window test. The index narrowed by calendar day; this is what actually decides
            // membership, and it stays keyed on ReminderTimeCalculator so the timing rule lives in one place.
            var deliveryTime = ReminderTimeCalculator.Calculate(candidate.When, candidate.Reminder);
            if (deliveryTime is null || deliveryTime.Value <= now || deliveryTime.Value > windowEnd)
                continue;

            var tag = TagForTask(candidate.Id);

            // For pre-reminders, include the actual scheduled wall-clock time so the toast can show it.
            // AtTime reminders fire at the task time itself, so there's nothing extra to display.
            DateTimeOffset? scheduledTime = candidate.Reminder != ReminderTiming.AtTime
                ? candidate.When.Date!.Value.ToLocal()
                : null;

            expected[tag] = new ScheduledNotification(
                deliveryTime.Value,
                tag,
                candidate.Title,
                candidate.TaskGroupName,
                scheduledTime,
                candidate.Reminder,
                $"action=complete&taskId={candidate.Id:D}");
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
            var enabled = _preferences.NotificationsEnabled;
            var candidates = enabled
                ? await ReadCandidatesAsync(cancellationToken).ConfigureAwait(false)
                : (IReadOnlyList<ReminderCandidate>)[];
            var liveRefs = enabled
                ? await _index.GetReminderTaskRefsAsync(cancellationToken).ConfigureAwait(false)
                : (IReadOnlyList<ReminderTaskRef>)[];

            var expected = enabled
                ? await BuildExpectedCoreAsync(candidates, cancellationToken).ConfigureAwait(false)
                : new Dictionary<string, ScheduledNotification>(StringComparer.Ordinal);
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

            // Delivered toasts leave the schedule queue and live in the notification center, so the diff
            // above never sees them. A toast that has already fired and whose task is THEN completed or
            // deleted in-app is in neither `expected` (dropped) nor `actual` (already gone from the
            // queue) — its stale toast would linger in the notification center, showing a reminder for a
            // task the user has resolved. Compare the notification center against live task truth so a
            // resolved task's delivered toast is revoked too. Scoped to task tags: a delivered toast for
            // a live, still-incomplete task is a legitimate reminder and must stay. Occurrence tags are
            // left to the recurring source, which owns per-cycle liveness.
            var liveTaskTags = BuildLiveTaskTags(liveRefs);
            foreach (var tag in _toasts.GetHistoryTags()
                         .Where(tag => tag.StartsWith(TaskTagPrefix, StringComparison.Ordinal) &&
                                       !liveTaskTags.Contains(tag))
                         .ToArray())
            {
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

    /// <summary>Tags for tasks that still warrant a visible notification — alive, not completed, carrying a
    /// reminder, and non-recurring (recurring series use occurrence tags). A delivered toast whose tag is
    /// absent here refers to a task the user has since resolved, so its notification-center entry is stale.
    /// Not windowed: a live task's tag stays "live" even after its delivery time passes, which is exactly
    /// the fired-but-unresolved reminder that must remain in the notification center.</summary>
    private static HashSet<string> BuildLiveTaskTags(IReadOnlyList<ReminderTaskRef> refs)
    {
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in refs)
        {
            // The index query already applied alive / open / reminder-on; only the recurring split is left.
            if (reference.IsRecurring)
                continue;

            live.Add(TagForTask(reference.Id));
        }

        return live;
    }

    public static string TagForTask(Guid taskId) => $"{TaskTagPrefix}{taskId:N}";

    /// <summary>The managed tag for one recurring occurrence, derived from its deterministic occurrence id
    /// (see <c>RecurrenceOccurrenceId</c>). Keeping the tag id-derived makes the diff idempotent and lets a
    /// per-cycle completion — which advances the series past that occurrence — drop exactly its reservation.</summary>
    public static string TagForOccurrence(Guid occurrenceId) => $"{OccurrenceTagPrefix}{occurrenceId:N}";

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
