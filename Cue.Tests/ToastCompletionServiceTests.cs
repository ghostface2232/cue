using Cue.Domain;
using Cue.Services;
using Cue.Storage;
using Cue.Storage.Recurrence;

namespace Cue.Tests;

/// <summary>
/// The headless 할 일 완료 path. A delivered toast sits in the notification center indefinitely while the
/// record behind it keeps moving, so by the time the button is pressed the payload may name a cycle the
/// series has already advanced past, or a task that is finished or gone. Refusing to write on a stale
/// payload is the correct half of that; finishing the interaction anyway — retiring the notification the
/// user pressed and saying why nothing was completed — is the half these tests pin down, because the
/// button press is the whole interaction when there is no window to show anything else in.
/// </summary>
public class ToastCompletionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Complete_PerformsTheCycle_WhenThePayloadNamesTheCurrentCycle()
    {
        using var temp = new TempDirectory();
        await using var harness = await Harness.OpenAsync(temp);
        var task = DailyRecurringTask(Now.AddHours(1));
        await harness.Store.SaveAsync(task);
        var cycleId = CurrentCycleId(task);

        var outcome = await harness.Service.CompleteAsync(
            new ToastActivationPayload(ToastAction.Complete, task.Id, cycleId));

        Assert.Equal(ToastCompletionOutcome.Completed, outcome);
        Assert.Equal(1, await harness.Store.GetOccurrenceCountAsync(task.Id));
        // The series advanced past the performed cycle and stayed open — performing a cycle never
        // completes the series, and the tag the toast carried is no longer the current one.
        var saved = await harness.Store.GetAsync<TaskItem>(task.Id);
        Assert.False(saved!.IsCompleted);
        Assert.NotEqual(cycleId, CurrentCycleId(saved));
        // Nothing to explain: the button did exactly what it says.
        Assert.Empty(harness.Toasts.Shown);
    }

    /// <summary>
    /// The case the guard exists for, and the one it used to answer with silence: the user performs the
    /// cycle in the app, the series moves on, and the already-delivered toast for the old cycle is still
    /// sitting in the notification center when they press 완료 on it.
    /// </summary>
    [Fact]
    public async Task StaleCycle_WritesNothing_ButRetiresItsToastAndSaysWhy()
    {
        using var temp = new TempDirectory();
        await using var harness = await Harness.OpenAsync(temp);
        // The cycle sits an hour in the past: its toast has been delivered, so it lives in the notification
        // center rather than the schedule queue, which is why the ordinary diff cannot reach it.
        var task = DailyRecurringTask(Now.AddHours(-1));
        await harness.Store.SaveAsync(task);
        var firedCycleId = CurrentCycleId(task);
        var firedTag = NotificationScheduler.TagForOccurrence(firedCycleId);
        harness.Toasts.Deliver(firedTag);

        // The user performs this cycle inside the app; the series advances past it.
        await harness.Recurrence.CompleteAsync(task.Id, Now);
        var advancedWhen = (await harness.Store.GetAsync<TaskItem>(task.Id))!.When.Date!.Value.Utc;

        var outcome = await harness.Service.CompleteAsync(
            new ToastActivationPayload(ToastAction.Complete, task.Id, firedCycleId));

        Assert.Equal(ToastCompletionOutcome.AlreadyResolved, outcome);
        // No second cycle recorded, and the series did not roll forward a second time.
        Assert.Equal(1, await harness.Store.GetOccurrenceCountAsync(task.Id));
        Assert.Equal(advancedWhen, (await harness.Store.GetAsync<TaskItem>(task.Id))!.When.Date!.Value.Utc);
        // The notification the user pressed is gone from the center...
        Assert.Contains(firedTag, harness.Toasts.HistoryRemovedTags);
        Assert.DoesNotContain(firedTag, harness.Toasts.GetHistoryTags());
        // ...and the press produced visible feedback instead of nothing at all.
        var feedback = Assert.Single(harness.Toasts.Shown);
        Assert.Equal(task.Title, feedback.Title);
        Assert.Contains("회차", feedback.Body);
    }

    /// <summary>
    /// A series that lost its recurrence rule has no cycle for the payload to match, so the toast names a
    /// cycle that no longer exists. Same treatment as an advanced one — the completion would land on the
    /// wrong thing.
    /// </summary>
    [Fact]
    public async Task OccurrencePayloadAgainstANonRecurringTask_IsTreatedAsStale()
    {
        using var temp = new TempDirectory();
        await using var harness = await Harness.OpenAsync(temp);
        var task = DailyRecurringTask(Now.AddHours(-1));
        var cycleId = CurrentCycleId(task);
        task.Recurrence = null;
        await harness.Store.SaveAsync(task);

        var outcome = await harness.Service.CompleteAsync(
            new ToastActivationPayload(ToastAction.Complete, task.Id, cycleId));

        Assert.Equal(ToastCompletionOutcome.AlreadyResolved, outcome);
        Assert.False((await harness.Store.GetAsync<TaskItem>(task.Id))!.IsCompleted);
        Assert.Single(harness.Toasts.Shown);
    }

    [Fact]
    public async Task ResolvedTask_RetiresItsToastAndSaysWhy()
    {
        using var temp = new TempDirectory();
        await using var harness = await Harness.OpenAsync(temp);
        var task = new TaskItem
        {
            Title = "이미 끝낸 일",
            When = ScheduledWhen.On(ZonedDateTime.FromUtc(Now.AddHours(-1), "UTC")),
            Reminder = ReminderTiming.AtTime,
        };
        await harness.Store.SaveAsync(task);
        var tag = NotificationScheduler.TagForTask(task.Id);
        harness.Toasts.Deliver(tag);

        // Completed in-app after its reminder had already fired.
        await harness.Recurrence.CompleteAsync(task.Id, Now);

        var outcome = await harness.Service.CompleteAsync(
            new ToastActivationPayload(ToastAction.Complete, task.Id, null));

        Assert.Equal(ToastCompletionOutcome.AlreadyResolved, outcome);
        // The first completion's instant survives — no second stamp over a finished task.
        Assert.Equal(Now, (await harness.Store.GetAsync<TaskItem>(task.Id))!.CompletedAt);
        Assert.DoesNotContain(tag, harness.Toasts.GetHistoryTags());
        var feedback = Assert.Single(harness.Toasts.Shown);
        Assert.Equal(task.Title, feedback.Title);
    }

    /// <summary>A record deleted outside the app leaves no title to put on the toast's first line, so the
    /// feedback has to stand on its own rather than showing an empty heading.</summary>
    [Fact]
    public async Task MissingTask_StillProducesFeedback_WithoutATitleToShow()
    {
        using var temp = new TempDirectory();
        await using var harness = await Harness.OpenAsync(temp);

        var outcome = await harness.Service.CompleteAsync(
            new ToastActivationPayload(ToastAction.Complete, Guid.NewGuid(), null));

        Assert.Equal(ToastCompletionOutcome.AlreadyResolved, outcome);
        var feedback = Assert.Single(harness.Toasts.Shown);
        Assert.False(string.IsNullOrWhiteSpace(feedback.Title));
        Assert.False(string.IsNullOrWhiteSpace(feedback.Body));
    }

    /// <summary>
    /// The stale path used to return before reaching any reconcile at all. It runs one now, which matters
    /// precisely because a stale entry survived this long: the app was closed, so no periodic pass swept
    /// the center, and its siblings are stale for the same reason.
    /// </summary>
    [Fact]
    public async Task StaleActivation_SweepsTheRestOfTheNotificationCenterToo()
    {
        using var temp = new TempDirectory();
        await using var harness = await Harness.OpenAsync(temp);
        var stale = DailyRecurringTask(Now.AddHours(-1));
        await harness.Store.SaveAsync(stale);
        var staleCycleId = CurrentCycleId(stale);
        harness.Toasts.Deliver(NotificationScheduler.TagForOccurrence(staleCycleId));
        await harness.Recurrence.CompleteAsync(stale.Id, Now);

        // An unrelated one-off whose reminder fired and which was then completed in-app. Nothing has swept
        // its delivered toast, because the app has not run a reconcile pass since.
        var sibling = new TaskItem
        {
            Title = "다른 할 일",
            When = ScheduledWhen.On(ZonedDateTime.FromUtc(Now.AddHours(-2), "UTC")),
            Reminder = ReminderTiming.AtTime,
        };
        await harness.Store.SaveAsync(sibling);
        var siblingTag = NotificationScheduler.TagForTask(sibling.Id);
        harness.Toasts.Deliver(siblingTag);
        await harness.Recurrence.CompleteAsync(sibling.Id, Now);

        await harness.Service.CompleteAsync(
            new ToastActivationPayload(ToastAction.Complete, stale.Id, staleCycleId));

        Assert.DoesNotContain(siblingTag, harness.Toasts.GetHistoryTags());
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static Guid CurrentCycleId(TaskItem task)
        => RecurrenceOccurrenceId.From(task.Id, task.When.Date!.Value.Utc);

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

    /// <summary>The real store, index, recurrence service and scheduler — only the OS notification channel
    /// is faked, since it is the one thing the service's stale path is judged on.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private Harness(
            IndexedTaskStore store,
            RecurringTaskService recurrence,
            NotificationScheduler scheduler,
            RecordingToastPresenter toasts)
        {
            Store = store;
            Recurrence = recurrence;
            Scheduler = scheduler;
            Toasts = toasts;
            Service = new ToastCompletionService(store, recurrence, scheduler, toasts, new FixedTimeProvider(Now));
        }

        public IndexedTaskStore Store { get; }
        public RecurringTaskService Recurrence { get; }
        public NotificationScheduler Scheduler { get; }
        public RecordingToastPresenter Toasts { get; }
        public ToastCompletionService Service { get; }

        public static async Task<Harness> OpenAsync(TempDirectory temp)
        {
            var clock = new FixedTimeProvider(Now);
            var store = await IndexedTaskStore.OpenAsync(
                new FileTaskStoreOptions
                {
                    RootPath = temp.Path,
                    IndexPath = Path.Combine(temp.Path, "index.db"),
                },
                clock,
                TimeZoneInfo.Utc);
            var recurrence = new RecurringTaskService(store);
            var toasts = new RecordingToastPresenter();
            var scheduler = new NotificationScheduler(
                store,
                store,
                toasts,
                new AlwaysEnabledNotificationPreferences(),
                clock,
                recurringSources: new[] { new RecurringNotificationSource(store, store, recurrence) },
                debounce: TimeSpan.Zero,
                periodicInterval: Timeout.InfiniteTimeSpan);
            return new Harness(store, recurrence, scheduler, toasts);
        }

        public async ValueTask DisposeAsync()
        {
            await Scheduler.DisposeAsync();
            await Store.DisposeAsync();
        }
    }

    private sealed record ShownToast(string Title, string Body);

    private sealed class RecordingToastPresenter : IToastPresenter
    {
        private readonly Dictionary<string, ToastScheduleRequest> _scheduled = new(StringComparer.Ordinal);
        private readonly HashSet<string> _history = new(StringComparer.Ordinal);

        public List<string> HistoryRemovedTags { get; } = [];
        public List<ShownToast> Shown { get; } = [];

        public void Schedule(ToastScheduleRequest request) => _scheduled[request.Tag] = request;

        public void CancelScheduled(string tag) => _scheduled.Remove(tag);

        public void RemoveFromHistory(string tag)
        {
            HistoryRemovedTags.Add(tag);
            _history.Remove(tag);
        }

        public IReadOnlyList<string> GetScheduledTags() => _scheduled.Keys.ToArray();

        public IReadOnlyList<string> GetHistoryTags() => _history.ToArray();

        /// <summary>Models the OS having delivered a toast: it is in the notification center, not the
        /// schedule queue, which is the state the stale-activation path actually meets.</summary>
        public void Deliver(string tag)
        {
            _scheduled.Remove(tag);
            _history.Add(tag);
        }

        public void Show(string title, string body, string? activationArguments = null)
            => Shown.Add(new ShownToast(title, body));
    }

    private sealed class AlwaysEnabledNotificationPreferences : INotificationPreferences
    {
        public bool NotificationsEnabled => true;

        public event EventHandler? NotificationsEnabledChanged
        {
            add { }
            remove { }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
