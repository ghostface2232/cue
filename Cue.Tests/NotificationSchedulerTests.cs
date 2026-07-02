using Cue.Domain;
using Cue.Services;
using Cue.Storage;
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
        var source = new RecurringNotificationSource(store, recurrence);
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
            => HistoryRemovedTags.Add(tag);

        public IReadOnlyList<string> GetScheduledTags() => Scheduled.Keys.ToArray();

        public void Show(string title, string body, string? activationArguments = null)
        {
        }
    }

    private sealed class FakeTaskStore(
        IEnumerable<TaskItem> tasks,
        IEnumerable<TaskGroup> groups) : ITaskStore, ITaskStoreChangeSource
    {
        private readonly List<TaskItem> _tasks = tasks.ToList();
        private readonly List<TaskGroup> _groups = groups.ToList();

        public event EventHandler? Changed;

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
