using Cue.Domain;
using Cue.Services;
using Cue.Storage;
using Cue.Storage.Index;
using Cue.Storage.Recurrence;

namespace Cue.Tests;

public class NotificationSchedulerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExpectedSet_ExcludesPastDelivery()
    {
        var task = TimedTask(Now.AddMinutes(-1));
        var (scheduler, _) = Create(task);
        await using (scheduler)
            Assert.Empty(await scheduler.BuildExpectedAsync());
    }

    [Fact]
    public async Task ExpectedSet_ExcludesNoneReminder()
    {
        var task = TimedTask(Now.AddHours(1));
        task.Reminder = ReminderTiming.None;
        var (scheduler, _) = Create(task);
        await using (scheduler)
            Assert.Empty(await scheduler.BuildExpectedAsync());
    }

    [Fact]
    public async Task ExpectedSet_ExcludesAllDayTask()
    {
        var task = TimedTask(Now.AddHours(1));
        task.When = ScheduledWhen.AllDay(
            ZonedDateTime.FromUtc(Now.AddHours(1), "UTC"));
        var (scheduler, _) = Create(task);
        await using (scheduler)
            Assert.Empty(await scheduler.BuildExpectedAsync());
    }

    [Fact]
    public async Task ExpectedSet_ExcludesDeliveryBeyondRollingWindow()
    {
        var task = TimedTask(Now.AddDays(14).AddMinutes(1));
        var (scheduler, _) = Create(task);
        await using (scheduler)
            Assert.Empty(await scheduler.BuildExpectedAsync());
    }

    [Fact]
    public async Task ExpectedSet_LeavesRecurringTaskForExtensionSource()
    {
        var task = TimedTask(Now.AddHours(1));
        task.Recurrence = new RecurrenceRule("FREQ=DAILY", task.When.Date!.Value);
        var (scheduler, _) = Create(task);
        await using (scheduler)
            Assert.Empty(await scheduler.BuildExpectedAsync());
    }

    [Fact]
    public async Task ExpectedSet_IncludesGroupNameWhenPresent()
    {
        var group = new TaskGroup { Name = "업무" };
        var task = TimedTask(Now.AddHours(1));
        task.TaskGroupId = group.Id;
        var (scheduler, _) = Create(new TaskItem[] { task }, new TaskGroup[] { group });
        await using (scheduler)
        {
            var expected = await scheduler.BuildExpectedAsync();
            var entry = Assert.Single(expected);
            Assert.Equal("업무", entry.Value.GroupName);
        }
    }

    [Fact]
    public async Task ExpectedSet_GroupNameIsNullWhenNoGroup()
    {
        var task = TimedTask(Now.AddHours(1));
        var (scheduler, _) = Create(task);
        await using (scheduler)
        {
            var expected = await scheduler.BuildExpectedAsync();
            var entry = Assert.Single(expected);
            Assert.Null(entry.Value.GroupName);
        }
    }

    [Fact]
    public async Task ExpectedSet_PreReminderCarriesScheduledTime()
    {
        var scheduledUtc = Now.AddHours(1);
        var task = TimedTask(scheduledUtc);
        task.Reminder = ReminderTiming.TenMinutesBefore;
        var (scheduler, _) = Create(task);
        await using (scheduler)
        {
            var expected = await scheduler.BuildExpectedAsync();
            var entry = Assert.Single(expected);
            Assert.NotNull(entry.Value.ScheduledTime);
            Assert.Equal(ReminderTiming.TenMinutesBefore, entry.Value.Reminder);
        }
    }

    [Fact]
    public async Task ExpectedSet_AtTimeReminderHasNoScheduledTime()
    {
        var task = TimedTask(Now.AddHours(1));
        task.Reminder = ReminderTiming.AtTime;
        var (scheduler, _) = Create(task);
        await using (scheduler)
        {
            var expected = await scheduler.BuildExpectedAsync();
            var entry = Assert.Single(expected);
            Assert.Null(entry.Value.ScheduledTime);
            Assert.Equal(ReminderTiming.AtTime, entry.Value.Reminder);
        }
    }

    [Fact]
    public async Task Reconcile_RemovesCompletedTaskReservation()
    {
        var task = TimedTask(Now.AddHours(1));
        var (scheduler, toasts) = Create(task);
        await using (scheduler)
        {
            await scheduler.ReconcileAsync();
            Assert.Contains(NotificationScheduler.TagForTask(task.Id), toasts.Scheduled);

            task.CompletedAt = Now;
            await scheduler.ReconcileAsync();

            Assert.Empty(toasts.Scheduled);
            Assert.Contains(NotificationScheduler.TagForTask(task.Id), toasts.CancelledTags);
        }
    }

    [Fact]
    public async Task Reconcile_RemovesCompletedTaskFromHistory()
    {
        var task = TimedTask(Now.AddHours(1));
        var (scheduler, toasts) = Create(task);
        await using (scheduler)
        {
            await scheduler.ReconcileAsync();

            task.CompletedAt = Now;
            await scheduler.ReconcileAsync();

            Assert.Contains(NotificationScheduler.TagForTask(task.Id), toasts.HistoryRemovedTags);
        }
    }

    [Fact]
    public async Task Reconcile_RemovesFiredToastFromHistory_WhenTaskCompletedAfterDelivery()
    {
        var task = TimedTask(Now.AddHours(1));
        var (scheduler, toasts) = Create(task);
        await using (scheduler)
        {
            await scheduler.ReconcileAsync();
            var tag = NotificationScheduler.TagForTask(task.Id);
            Assert.Contains(tag, toasts.Scheduled);

            // The OS delivers the toast: it leaves the schedule queue and enters the notification center.
            toasts.Fire(tag);
            Assert.DoesNotContain(tag, toasts.GetScheduledTags());
            Assert.Contains(tag, toasts.GetHistoryTags());

            // The user completes the task in-app afterwards. Its tag is now in neither the expected set
            // nor the schedule queue, so only the history pass can revoke the delivered toast.
            task.CompletedAt = Now;
            await scheduler.ReconcileAsync();

            Assert.Contains(tag, toasts.HistoryRemovedTags);
            Assert.DoesNotContain(tag, toasts.GetHistoryTags());
        }
    }

    [Fact]
    public async Task Reconcile_RemovesFiredToastFromHistory_WhenTaskDeletedAfterDelivery()
    {
        var task = TimedTask(Now.AddHours(1));
        var (scheduler, toasts) = Create(task);
        await using (scheduler)
        {
            await scheduler.ReconcileAsync();
            var tag = NotificationScheduler.TagForTask(task.Id);
            toasts.Fire(tag);

            task.DeletedAt = Now;
            await scheduler.ReconcileAsync();

            Assert.Contains(tag, toasts.HistoryRemovedTags);
            Assert.DoesNotContain(tag, toasts.GetHistoryTags());
        }
    }

    [Fact]
    public async Task Reconcile_KeepsFiredToastInHistory_WhenTaskStillPending()
    {
        // Delivery already in the past, so the expected set excludes it — modeling a toast that has
        // fired. The task is still incomplete, so its delivered reminder must survive reconcile.
        var task = TimedTask(Now.AddMinutes(-5));
        var (scheduler, toasts) = Create(task);
        await using (scheduler)
        {
            var tag = NotificationScheduler.TagForTask(task.Id);
            toasts.Fire(tag);

            await scheduler.ReconcileAsync();

            Assert.DoesNotContain(tag, toasts.HistoryRemovedTags);
            Assert.Contains(tag, toasts.GetHistoryTags());
        }
    }

    [Fact]
    public async Task Reconcile_TimeChange_CancelsAndRegistersSameTagAtNewTime()
    {
        var task = TimedTask(Now.AddHours(1));
        var (scheduler, toasts) = Create(task);
        await using (scheduler)
        {
            await scheduler.ReconcileAsync();
            task.When = ScheduledWhen.On(
                ZonedDateTime.FromUtc(Now.AddHours(2), "UTC"));

            await scheduler.ReconcileAsync();

            var tag = NotificationScheduler.TagForTask(task.Id);
            Assert.Equal(2, toasts.ScheduleCount);
            Assert.Contains(tag, toasts.CancelledTags);
            Assert.Equal(Now.AddHours(2), toasts.Scheduled[tag].DeliveryTime);
        }
    }

    [Fact]
    public async Task Reconcile_SameInputTwice_IsIdempotent()
    {
        var task = TimedTask(Now.AddHours(1));
        var (scheduler, toasts) = Create(task);
        await using (scheduler)
        {
            await scheduler.ReconcileAsync();
            await scheduler.ReconcileAsync();

            Assert.Equal(1, toasts.ScheduleCount);
            Assert.Empty(toasts.CancelledTags);
            Assert.Single(toasts.Scheduled);
        }
    }

    [Fact]
    public async Task Reconcile_MasterOff_CancelsEveryManagedReservation()
    {
        var first = TimedTask(Now.AddHours(1));
        var second = TimedTask(Now.AddHours(2));
        var preferences = new FakeNotificationPreferences { NotificationsEnabled = true };
        var (scheduler, toasts) = Create(preferences, new[] { first, second }, []);
        await using (scheduler)
        {
            await scheduler.ReconcileAsync();
            Assert.Equal(2, toasts.Scheduled.Count);

            preferences.NotificationsEnabled = false;
            await scheduler.ReconcileAsync();

            Assert.Empty(toasts.Scheduled);
            Assert.Equal(2, toasts.CancelledTags.Distinct().Count());
        }
    }

    [Fact]
    public async Task StartAsync_PerformsInitialReconcile()
    {
        var task = TimedTask(Now.AddHours(1));
        var (scheduler, toasts) = Create(task);
        await using (scheduler)
        {
            await scheduler.StartAsync();

            Assert.Contains(NotificationScheduler.TagForTask(task.Id), toasts.Scheduled);
        }
    }

    [Fact]
    public async Task SaveEvent_TriggersDebouncedReconcile()
    {
        var store = new FakeTaskStore([], []);
        var toasts = new FakeToastPresenter();
        var scheduler = new NotificationScheduler(
            store,
            store,
            toasts,
            new FakeNotificationPreferences { NotificationsEnabled = true },
            new FixedTimeProvider(Now),
            debounce: TimeSpan.Zero,
            periodicInterval: Timeout.InfiniteTimeSpan);
        await using (scheduler)
        {
            await scheduler.StartAsync();
            var task = TimedTask(Now.AddHours(1));

            await store.SaveAsync(task);
            await toasts.ScheduleObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Contains(NotificationScheduler.TagForTask(task.Id), toasts.Scheduled);
        }
    }

    // ── Recurring occurrence scheduling (Step 6) ────────────────────────────

    [Fact]
    public async Task Recurring_DailyTask_SchedulesEveryOccurrenceInWindow()
    {
        // Daily at Now+1h; the 14-day window (exclusive of now, inclusive of now+14d) holds 14 firings.
        var task = DailyRecurringTask(Now.AddHours(1));
        var (scheduler, toasts, _, _) = CreateWithRecurring(task);
        await using (scheduler)
        {
            await scheduler.ReconcileAsync();

            Assert.Equal(14, toasts.Scheduled.Count);
            Assert.All(toasts.Scheduled.Keys, tag => Assert.StartsWith("occurrence-", tag));
        }
    }

    [Fact]
    public async Task Recurring_CompletingTodayOccurrence_DropsOnlyThatReservation()
    {
        var task = DailyRecurringTask(Now.AddHours(1));
        var (scheduler, toasts, _, recurrence) = CreateWithRecurring(task);
        await using (scheduler)
        {
            await scheduler.ReconcileAsync();
            var todayTag = NotificationScheduler.TagForOccurrence(
                RecurrenceOccurrenceId.From(task.Id, task.When.Date!.Value.Utc));
            Assert.Equal(14, toasts.Scheduled.Count);
            Assert.Contains(todayTag, toasts.Scheduled);

            // Perform the current cycle: the series advances one day, so today's occurrence leaves the
            // expected set and the diff cancels exactly its reservation — the rest stay put.
            await recurrence.CompleteAsync(task.Id, Now);
            await scheduler.ReconcileAsync();

            Assert.Equal(13, toasts.Scheduled.Count);
            Assert.DoesNotContain(todayTag, toasts.Scheduled.Keys);
            Assert.Contains(todayTag, toasts.CancelledTags);
        }
    }

    [Fact]
    public async Task Recurring_SeriesTimeChange_ReregistersEveryOccurrence()
    {
        var task = DailyRecurringTask(Now.AddHours(1));
        var (scheduler, toasts, _, _) = CreateWithRecurring(task);
        await using (scheduler)
        {
            await scheduler.ReconcileAsync();
            var originalTags = toasts.Scheduled.Keys.ToArray();
            Assert.Equal(14, originalTags.Length);

            // Shift the whole series two hours later (re-anchor + move the current cycle): every occurrence
            // instant moves, so every occurrence id — and thus every tag — changes.
            var shifted = ZonedDateTime.FromUtc(Now.AddHours(3), "UTC");
            task.When = ScheduledWhen.On(shifted);
            task.Recurrence = new RecurrenceRule("FREQ=DAILY", shifted);

            await scheduler.ReconcileAsync();

            Assert.Equal(14, toasts.Scheduled.Count);
            Assert.All(originalTags, tag => Assert.Contains(tag, toasts.CancelledTags));
            Assert.All(originalTags, tag => Assert.DoesNotContain(tag, toasts.Scheduled.Keys));
            Assert.Equal(28, toasts.ScheduleCount); // 14 original + 14 re-registered at the new time
        }
    }

    private static TaskItem TimedTask(DateTimeOffset when) => new()
    {
        Title = "알림 테스트",
        When = ScheduledWhen.On(ZonedDateTime.FromUtc(when, "UTC")),
        Reminder = ReminderTiming.AtTime,
    };

    private static TaskItem DailyRecurringTask(DateTimeOffset firstOccurrence)
    {
        var when = ZonedDateTime.FromUtc(firstOccurrence, "UTC");
        return new TaskItem
        {
            Title = "매일 알림",
            When = ScheduledWhen.On(when),
            Reminder = ReminderTiming.AtTime,
            Recurrence = new RecurrenceRule("FREQ=DAILY", when),
        };
    }

    private static (NotificationScheduler Scheduler, FakeToastPresenter Toasts, FakeTaskStore Store, RecurringTaskService Recurrence)
        CreateWithRecurring(params TaskItem[] tasks)
    {
        var store = new FakeTaskStore(tasks, []);
        var toasts = new FakeToastPresenter();
        var recurrence = new RecurringTaskService(store);
        var source = new RecurringNotificationSource(store, store, recurrence);
        var scheduler = new NotificationScheduler(
            store,
            store,
            toasts,
            new FakeNotificationPreferences { NotificationsEnabled = true },
            new FixedTimeProvider(Now),
            recurringSources: new[] { source },
            debounce: TimeSpan.Zero,
            periodicInterval: Timeout.InfiniteTimeSpan);
        return (scheduler, toasts, store, recurrence);
    }

    private static (NotificationScheduler Scheduler, FakeToastPresenter Toasts) Create(params TaskItem[] tasks)
        => Create(new FakeNotificationPreferences { NotificationsEnabled = true }, tasks, []);

    private static (NotificationScheduler Scheduler, FakeToastPresenter Toasts) Create(
        TaskItem[] tasks,
        TaskGroup[] groups)
        => Create(new FakeNotificationPreferences { NotificationsEnabled = true }, tasks, groups);

    private static (NotificationScheduler Scheduler, FakeToastPresenter Toasts) Create(
        FakeNotificationPreferences preferences,
        TaskItem[] tasks,
        TaskGroup[] groups)
    {
        var store = new FakeTaskStore(tasks, groups);
        var toasts = new FakeToastPresenter();
        var scheduler = new NotificationScheduler(
            store,
            store,
            toasts,
            preferences,
            new FixedTimeProvider(Now),
            debounce: TimeSpan.Zero,
            periodicInterval: Timeout.InfiniteTimeSpan);
        return (scheduler, toasts);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeNotificationPreferences : INotificationPreferences
    {
        private bool _enabled;

        public bool NotificationsEnabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                NotificationsEnabledChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public event EventHandler? NotificationsEnabledChanged;
    }

    private sealed class FakeToastPresenter : IToastPresenter
    {
        public Dictionary<string, ScheduledNotification> Scheduled { get; } = new(StringComparer.Ordinal);
        public HashSet<string> History { get; } = new(StringComparer.Ordinal);
        public List<string> CancelledTags { get; } = [];
        public List<string> HistoryRemovedTags { get; } = [];
        public int ScheduleCount { get; private set; }
        public TaskCompletionSource ScheduleObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Schedule(ToastScheduleRequest request)
        {
            ScheduleCount++;
            Scheduled[request.Tag] = new ScheduledNotification(
                request.DeliveryTime,
                request.Tag,
                request.Title,
                request.GroupName,
                request.ScheduledTime,
                request.Reminder,
                request.CompletionArguments);
            ScheduleObserved.TrySetResult();
        }

        public void CancelScheduled(string tag)
        {
            CancelledTags.Add(tag);
            Scheduled.Remove(tag);
        }

        public void RemoveFromHistory(string tag)
        {
            HistoryRemovedTags.Add(tag);
            History.Remove(tag);
        }

        public IReadOnlyList<string> GetScheduledTags() => Scheduled.Keys.ToArray();

        public IReadOnlyList<string> GetHistoryTags() => History.ToArray();

        /// <summary>Models the OS delivering a scheduled toast: it leaves the schedule queue and enters
        /// the notification center (Action Center).</summary>
        public void Fire(string tag)
        {
            Scheduled.Remove(tag);
            History.Add(tag);
        }

        public void Show(string title, string body, string? activationArguments = null)
        {
        }
    }

    /// <summary>
    /// Stands in for the store <i>and</i> its index. The scheduler builds its expected set from the index
    /// (so a reconcile pass reads no record files), so the reminder projections here must mirror the SQL in
    /// <c>SqliteTaskIndex</c> exactly — same predicate, same local-day range test — or these tests would
    /// stop covering the filter that actually ships. Index members the notification path never touches
    /// throw rather than returning a plausible empty list, so a future caller can't silently get nothing.
    /// </summary>
    private sealed class FakeTaskStore(
        IEnumerable<TaskItem> tasks,
        IEnumerable<TaskGroup> groups) : ITaskStore, ITaskStoreChangeSource, ITaskIndex
    {
        private readonly List<TaskItem> _tasks = tasks.ToList();
        private readonly List<TaskGroup> _groups = groups.ToList();

        public event EventHandler? Changed;

        // ── ITaskIndex: the reminder surface the scheduler actually uses ──

        // Mirrors GetReminderCandidatesAsync: alive, open, non-recurring, reminder not None, a timed
        // OnDate, and the pinned local calendar day inside the inclusive range.
        public Task<IReadOnlyList<ReminderCandidate>> GetReminderCandidatesAsync(
            DateOnly rangeStart, DateOnly rangeEnd, CancellationToken cancellationToken = default)
        {
            var groupNames = _groups.Where(g => !g.IsDeleted).ToDictionary(g => g.Id, g => g.Name);
            var candidates = _tasks
                .Where(t => !t.IsDeleted && !t.IsCompleted && t.Recurrence is null &&
                            t.Reminder != ReminderTiming.None &&
                            t.When.Kind == WhenKind.OnDate && !t.When.IsAllDay && t.When.Date is not null)
                .Where(t =>
                {
                    var day = DateOnly.FromDateTime(t.When.Date!.Value.ToLocal().DateTime);
                    return day >= rangeStart && day <= rangeEnd;
                })
                .Select(t => new ReminderCandidate(
                    t.Id,
                    t.Title,
                    t.TaskGroupId is { } gid && groupNames.TryGetValue(gid, out var name) ? name : null,
                    t.When,
                    t.Reminder))
                .ToArray();
            return Task.FromResult<IReadOnlyList<ReminderCandidate>>(candidates);
        }

        // Mirrors GetReminderTaskRefsAsync: alive, open, reminder not None — deliberately un-windowed.
        public Task<IReadOnlyList<ReminderTaskRef>> GetReminderTaskRefsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ReminderTaskRef>>(_tasks
                .Where(t => !t.IsDeleted && !t.IsCompleted && t.Reminder != ReminderTiming.None)
                .Select(t => new ReminderTaskRef(t.Id, t.Recurrence is not null))
                .ToArray());

        // The recurring source reads group names for the toast's second line; tombstones are excluded here
        // just as the real query's "deleted_at IS NULL" does.
        public Task<IReadOnlyList<TaskGroupListItem>> GetTaskGroupsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TaskGroupListItem>>(_groups
                .Where(g => !g.IsDeleted)
                .Select(g => new TaskGroupListItem(g.Id, g.Name, g.Icon, g.SortOrder))
                .ToArray());

        // ── ITaskIndex: everything the notification path never asks for ──

        private static Task<T> Unused<T>([System.Runtime.CompilerServices.CallerMemberName] string member = "")
            => throw new NotSupportedException($"{member} is not part of the notification path.");

        public Task<IReadOnlyList<TagListItem>> GetTagsAsync(CancellationToken cancellationToken = default) => Unused<IReadOnlyList<TagListItem>>();
        public Task<IReadOnlyDictionary<Guid, int>> GetOpenTaskCountsByTaskGroupAsync(CancellationToken cancellationToken = default) => Unused<IReadOnlyDictionary<Guid, int>>();
        public Task<IReadOnlyDictionary<Guid, int>> GetOpenTaskCountsByTagAsync(CancellationToken cancellationToken = default) => Unused<IReadOnlyDictionary<Guid, int>>();
        public Task<int> GetOpenTaskCountWithoutTaskGroupAsync(CancellationToken cancellationToken = default) => Unused<int>();
        public Task<int> GetOpenTaskCountWithoutTagAsync(CancellationToken cancellationToken = default) => Unused<int>();
        public Task<IReadOnlyList<TaskListItem>> GetAllActiveAsync(bool keepCompletedToday = false, CancellationToken cancellationToken = default) => Unused<IReadOnlyList<TaskListItem>>();
        public Task<IReadOnlyList<TaskListItem>> GetByTaskGroupAsync(Guid taskGroupId, bool keepCompletedToday = false, CancellationToken cancellationToken = default) => Unused<IReadOnlyList<TaskListItem>>();
        public Task<IReadOnlyList<TaskListItem>> GetByTagAsync(Guid tagId, bool keepCompletedToday = false, CancellationToken cancellationToken = default) => Unused<IReadOnlyList<TaskListItem>>();
        public Task<IReadOnlyList<TaskListItem>> GetCompletedByTaskGroupAsync(Guid taskGroupId, int limit = int.MaxValue, int offset = 0, bool excludeKeptInPlace = false, CancellationToken cancellationToken = default) => Unused<IReadOnlyList<TaskListItem>>();
        public Task<IReadOnlyList<TaskListItem>> GetCompletedByTagAsync(Guid tagId, int limit = int.MaxValue, int offset = 0, bool excludeKeptInPlace = false, CancellationToken cancellationToken = default) => Unused<IReadOnlyList<TaskListItem>>();
        public Task<int> GetCompletedCountByTaskGroupAsync(Guid taskGroupId, bool excludeKeptInPlace = false, CancellationToken cancellationToken = default) => Unused<int>();
        public Task<int> GetCompletedCountByTagAsync(Guid tagId, bool excludeKeptInPlace = false, CancellationToken cancellationToken = default) => Unused<int>();
        public Task<IReadOnlyList<TaskListItem>> GetWithoutTaskGroupAsync(bool keepCompletedToday = false, CancellationToken cancellationToken = default) => Unused<IReadOnlyList<TaskListItem>>();
        public Task<IReadOnlyList<TaskListItem>> GetWithoutTagAsync(bool keepCompletedToday = false, CancellationToken cancellationToken = default) => Unused<IReadOnlyList<TaskListItem>>();
        public Task<IReadOnlyList<TaskListItem>> GetTodayAsync(bool keepCompletedToday = false, CancellationToken cancellationToken = default) => Unused<IReadOnlyList<TaskListItem>>();
        public Task<IReadOnlyList<TaskListItem>> GetTodayCompletedAsync(int limit = int.MaxValue, int offset = 0, bool excludeKeptInPlace = false, CancellationToken cancellationToken = default) => Unused<IReadOnlyList<TaskListItem>>();
        public Task<int> GetTodayCompletedCountAsync(bool excludeKeptInPlace = false, CancellationToken cancellationToken = default) => Unused<int>();
        public Task<IReadOnlyList<TaskListItem>> GetUpcomingAsync(bool keepCompletedToday = false, CancellationToken cancellationToken = default) => Unused<IReadOnlyList<TaskListItem>>();
        public Task<IReadOnlyList<TaskListItem>> GetAnytimeAsync(bool keepCompletedToday = false, CancellationToken cancellationToken = default) => Unused<IReadOnlyList<TaskListItem>>();
        public Task<IReadOnlyList<TaskListItem>> GetLogbookAsync(CancellationToken cancellationToken = default) => Unused<IReadOnlyList<TaskListItem>>();
        public Task<IReadOnlyList<TaskListItem>> GetByPriorityAsync(bool keepCompletedToday = false, CancellationToken cancellationToken = default) => Unused<IReadOnlyList<TaskListItem>>();
        public Task<IReadOnlyList<TaskListItem>> GetTimelineRowsAsync(DateOnly rangeStart, DateOnly rangeEnd, CancellationToken cancellationToken = default) => Unused<IReadOnlyList<TaskListItem>>();
        public Task<IReadOnlyList<OccurrenceListItem>> GetOccurrencesAsync(Guid seriesId, int limit = int.MaxValue, int offset = 0, CancellationToken cancellationToken = default) => Unused<IReadOnlyList<OccurrenceListItem>>();
        public Task<int> GetOccurrenceCountAsync(Guid seriesId, CancellationToken cancellationToken = default) => Unused<int>();

        // ── ITaskStore ──

        public Task<IReadOnlyList<T>> GetAllAsync<T>(CancellationToken cancellationToken = default)
            where T : RecordBase
        {
            if (typeof(T) == typeof(TaskItem))
                return Task.FromResult<IReadOnlyList<T>>(_tasks.Cast<T>().ToArray());
            if (typeof(T) == typeof(TaskGroup))
                return Task.FromResult<IReadOnlyList<T>>(_groups.Cast<T>().ToArray());
            return Task.FromResult<IReadOnlyList<T>>([]);
        }

        public Task<T?> GetAsync<T>(Guid id, CancellationToken cancellationToken = default)
            where T : RecordBase
            => Task.FromResult(_tasks.FirstOrDefault(task => task.Id == id) as T);

        public Task SaveAsync<T>(T record, CancellationToken cancellationToken = default)
            where T : RecordBase
        {
            if (record is TaskItem task)
            {
                var index = _tasks.FindIndex(item => item.Id == task.Id);
                if (index >= 0) _tasks[index] = task;
                else _tasks.Add(task);
            }
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task DeleteAsync<T>(Guid id, CancellationToken cancellationToken = default)
            where T : RecordBase
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }
}
