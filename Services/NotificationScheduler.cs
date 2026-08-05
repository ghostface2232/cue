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

/// <summary>What one recurring source contributes to a reconcile pass.</summary>
/// <param name="Expected">The occurrence toasts that should be registered in the rolling window —
/// windowed by delivery time, exactly like the one-off half of the expected set.</param>
/// <param name="LiveTags">Every occurrence tag whose cycle is still unresolved, <i>including</i> cycles
/// whose toast has already been delivered and therefore no longer appears in <paramref name="Expected"/>.
/// This is the occurrence counterpart of the live task-tag set: the scheduler sweeps a delivered toast
/// out of the notification center once its tag is absent here, which is what retires the reminder for a
/// cycle the user has since completed, skipped, or deleted.</param>
public sealed record RecurringNotificationSet(
    IReadOnlyList<ScheduledNotification> Expected,
    IReadOnlyCollection<string> LiveTags);

/// <summary>
/// Extension seam for recurring occurrences. The foundation scheduler excludes recurring tasks; a
/// recurring source contributes occurrence notifications here without changing one-off reconciliation.
/// Because an occurrence's liveness is per-cycle knowledge only the source has (the series record holds
/// one rule, not a row per cycle), the source also reports which occurrence tags are still live so the
/// scheduler can retire the rest — see <see cref="RecurringNotificationSet"/>.
/// </summary>
public interface IRecurringNotificationSource
{
    Task<RecurringNotificationSet> GetExpectedAsync(
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

    // How far past the rolling window the candidate day-range reaches. Two effects stack at the far end and
    // must both be paid for:
    //
    //   1. A pre-reminder leads its task by up to 24h (OneDayBefore), so a task sitting up to a day beyond
    //      the window can still deliver inside it: when_instant <= windowEnd + 24h.
    //   2. when_date is the calendar day in the *task's own* zone, which runs up to 14h ahead of UTC, so
    //      that already-late task's indexed day can be later still: when_date <= date(windowEnd + 38h).
    //
    // One day of slack pays for the lead alone and silently dropped the reminder for a far-eastern task
    // near the window edge (a UTC+14 task with OneDayBefore delivering inside the window, whose when_date
    // lands two UTC days past the window end). Two days covers 24h + 14h with room to spare. Widening is
    // free: this is a pre-filter over an indexed day column, and the per-candidate ReminderTimeCalculator
    // test below still decides membership — extra rows are discarded, never scheduled.
    private static readonly TimeSpan CandidateRangeSlack = TimeSpan.FromDays(2);

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
        return (await BuildExpectedCoreAsync(candidates, cancellationToken).ConfigureAwait(false)).Expected;
    }

    /// <summary>Queries the index for every task that could plausibly deliver inside the rolling window.
    /// The day range is deliberately wider than the window at each end — enough to cover a pre-reminder's
    /// lead <i>plus</i> a task zone's offset from UTC (see <see cref="CandidateRangeSlack"/>); membership is
    /// decided per candidate below.</summary>
    private Task<IReadOnlyList<ReminderCandidate>> ReadCandidatesAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        return _index.GetReminderCandidatesAsync(
            DateOnly.FromDateTime((now - CandidateRangeSlack).UtcDateTime),
            DateOnly.FromDateTime((now + RollingWindow + CandidateRangeSlack).UtcDateTime),
            cancellationToken);
    }

    /// <summary>Derives the expected set from an already-queried candidate list, so
    /// <see cref="ReconcileAsync"/> can hit the index once and reuse the result. The live occurrence tags
    /// the recurring sources report travel out with it, since the same pass is what produces them.</summary>
    private async Task<(IReadOnlyDictionary<string, ScheduledNotification> Expected, HashSet<string> LiveOccurrenceTags)>
        BuildExpectedCoreAsync(
        IReadOnlyList<ReminderCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var expected = new Dictionary<string, ScheduledNotification>(StringComparer.Ordinal);
        var liveOccurrenceTags = new HashSet<string>(StringComparer.Ordinal);

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
            var contribution = await source.GetExpectedAsync(now, windowEnd, cancellationToken)
                .ConfigureAwait(false);
            foreach (var notification in contribution.Expected)
                expected[notification.Tag] = notification;
            foreach (var tag in contribution.LiveTags)
                liveOccurrenceTags.Add(tag);
        }

        return (expected, liveOccurrenceTags);
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

            IReadOnlyDictionary<string, ScheduledNotification> expected =
                new Dictionary<string, ScheduledNotification>(StringComparer.Ordinal);
            var liveOccurrenceTags = new HashSet<string>(StringComparer.Ordinal);
            if (enabled)
                (expected, liveOccurrenceTags) =
                    await BuildExpectedCoreAsync(candidates, cancellationToken).ConfigureAwait(false);

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
            // task the user has resolved. Compare the notification center against live truth so a
            // resolved task's delivered toast is revoked too, while a delivered toast for a still-open
            // task stays: that one is a legitimate reminder.
            //
            // Both tag kinds are swept the same way, from the same idea of "live", each sourced where the
            // knowledge is: a one-off's liveness is one indexed row (BuildLiveTaskTags), while a cycle's
            // liveness is per-occurrence and only the recurring source can compute it — completing a cycle
            // advances the series past it, so its tag simply stops being reported. Sweeping task tags alone
            // left a completed cycle's delivered toast in the notification center forever.
            var live = BuildLiveTaskTags(liveRefs);
            live.UnionWith(liveOccurrenceTags);
            foreach (var tag in _toasts.GetHistoryTags()
                         .Where(tag => IsManagedTag(tag) && !live.Contains(tag))
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
    /// reminder, and non-recurring (recurring series use occurrence tags, which the recurring source
    /// reports as its live set). A delivered toast whose tag is absent from the union of the two sets
    /// refers to work the user has since resolved, so its notification-center entry is stale.
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
