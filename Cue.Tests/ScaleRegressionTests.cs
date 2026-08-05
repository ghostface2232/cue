using Cue.Domain;
using Cue.Services;
using Cue.Storage;
using Cue.Storage.Index;
using Cue.Storage.Recurrence;

namespace Cue.Tests;

/// <summary>
/// Guards the shape of the hot paths' record-file access, which no correctness test can see.
/// </summary>
/// <remarks>
/// Two regressions this suite exists to catch, both of which shipped and were correct the whole time:
/// the notification reconcile loop enumerated the entire <c>tasks/</c> folder on every pass (and a pass
/// runs on every save), and saving one task read a record file per group and per tag reference. Every
/// assertion below counts reads rather than timing anything, so the suite is deterministic — a
/// wall-clock threshold would be flaky on a shared runner and would not say what actually went wrong.
/// <para>
/// The counting store wraps <see cref="FileTaskStore"/> rather than replacing it, so the records are real
/// and the index is built from real files. <see cref="IndexedTaskStore"/> reserves a few internal
/// fast-paths for a bare <c>FileTaskStore</c> (it reads through <c>ReadForReferenceValidationAsync</c> to
/// tell an absent record from an unreadable one); wrapping it takes the equivalent public path instead.
/// That distinction does not affect what is measured here — both paths perform exactly one read per
/// reference, and it is the <i>number</i> of reads, not their error semantics, that these tests pin.
/// </para>
/// </remarks>
public sealed class ScaleRegressionTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    // Large enough that an accidental O(n) read would blow every bound below by two orders of magnitude,
    // small enough to stay a fast test.
    private const int ListSize = 200;

    private readonly List<string> _roots = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        foreach (var root in _roots)
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
                // best-effort cleanup of temp test folders
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Saving one task must cost a fixed number of record reads no matter how long the list is. Before the
    /// index-first reference check this was 1 + 1 group + one per tag, on the critical path of every
    /// keystroke-driven autosave.
    /// </summary>
    [Fact]
    public async Task SavingOneTask_ReadsAFixedNumberOfRecords_RegardlessOfListSize()
    {
        var (store, counter, group, tags) = await OpenWithFixtureAsync();
        await using var owned = store;

        var task = new TaskItem
        {
            Title = "참조가 붙은 할 일",
            TaskGroupId = group.Id,
            TagIds = { tags[0].Id, tags[1].Id, tags[2].Id },
        };
        await store.SaveAsync(task);   // seed it so the edit below is a realistic re-save

        counter.Reset();
        task.Title = "제목 한 글자 수정";
        await store.SaveAsync(task);

        // One read: the save path re-reads the record it is about to overwrite so a stale in-memory copy
        // cannot resurrect a tombstone. The group and the three tags resolve from the index, so they cost
        // no file read at all.
        Assert.Equal(1, counter.RecordReads);
        Assert.Equal(0, counter.FolderScans);
    }

    /// <summary>
    /// The folder scan is the part that actually scaled, so assert its absence directly: no save may
    /// enumerate a record partition, at any list size.
    /// </summary>
    [Fact]
    public async Task SavingOneTask_NeverEnumeratesARecordFolder()
    {
        var (store, counter, group, tags) = await OpenWithFixtureAsync();
        await using var owned = store;

        counter.Reset();
        for (var i = 0; i < 5; i++)
        {
            await store.SaveAsync(new TaskItem
            {
                Title = $"추가 {i}",
                TaskGroupId = group.Id,
                TagIds = { tags[i % tags.Count].Id },
                When = ScheduledWhen.On(ZonedDateTime.FromUtc(Now.AddHours(i + 1), "UTC")),
            });
        }

        Assert.Equal(0, counter.FolderScans);
        // Five saves, one existing-record probe each — and nothing that grows with the fixture.
        Assert.Equal(5, counter.RecordReads);
    }

    /// <summary>
    /// A notification reconcile pass runs on every save (debounced) and every 15 minutes. It must build
    /// its expected set from the index: the only record files it may open are the recurring series, whose
    /// RRULE is deliberately not indexed.
    /// </summary>
    [Fact]
    public async Task NotificationReconcile_ReadsOnlyRecurringSeriesFiles()
    {
        var (store, counter, group, _) = await OpenWithFixtureAsync();
        await using var owned = store;

        const int recurringCount = 3;
        for (var i = 0; i < recurringCount; i++)
        {
            var anchor = ZonedDateTime.FromUtc(Now.AddHours(i + 1), "UTC");
            await store.SaveAsync(new TaskItem
            {
                Title = $"반복 {i}",
                TaskGroupId = group.Id,
                When = ScheduledWhen.On(anchor),
                Recurrence = new RecurrenceRule("FREQ=DAILY", anchor),
            });
        }

        var recurrence = new RecurringTaskService(store);
        var source = new RecurringNotificationSource(counter, store, recurrence);
        await using var scheduler = new NotificationScheduler(
            store,
            store,
            new NullToastPresenter(),
            new EnabledNotificationPreferences(),
            new FixedClock(Now),
            recurringSources: new[] { source },
            debounce: TimeSpan.Zero,
            periodicInterval: Timeout.InfiniteTimeSpan);

        counter.Reset();
        var expected = await scheduler.BuildExpectedAsync();

        // The one-off half is served entirely from the index.
        Assert.Equal(0, counter.FolderScans);
        // …and the recurring half opens exactly the series it was told about — not the 200-task fixture.
        Assert.Equal(recurringCount, counter.RecordReads);

        // Sanity: the pass really did produce toasts, so the counts above are not "zero work done".
        Assert.NotEmpty(expected);
    }

    /// <summary>The reconcile loop is re-entered constantly, so its cost must be flat across passes —
    /// a per-pass cost that grew with the fixture would be the original regression returning.</summary>
    [Fact]
    public async Task NotificationReconcile_CostsTheSameOnEveryPass()
    {
        var (store, counter, _, _) = await OpenWithFixtureAsync();
        await using var owned = store;

        var recurrence = new RecurringTaskService(store);
        var source = new RecurringNotificationSource(counter, store, recurrence);
        await using var scheduler = new NotificationScheduler(
            store,
            store,
            new NullToastPresenter(),
            new EnabledNotificationPreferences(),
            new FixedClock(Now),
            recurringSources: new[] { source },
            debounce: TimeSpan.Zero,
            periodicInterval: Timeout.InfiniteTimeSpan);

        counter.Reset();
        await scheduler.ReconcileAsync();
        var first = counter.RecordReads + counter.FolderScans;

        counter.Reset();
        await scheduler.ReconcileAsync();
        var second = counter.RecordReads + counter.FolderScans;

        Assert.Equal(first, second);
        Assert.Equal(0, first);   // no recurring series in this fixture, so nothing to open at all
    }

    /// <summary>Builds a realistic fixture: one group, three tags, and <see cref="ListSize"/> tasks spread
    /// over them, all written through the store so the index mirrors the files exactly.</summary>
    private async Task<(IndexedTaskStore Store, CountingTaskStore Counter, TaskGroup Group, IReadOnlyList<Tag> Tags)>
        OpenWithFixtureAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cue-scale-tests", Guid.NewGuid().ToString("N"));
        _roots.Add(root);

        var clock = new FixedClock(Now);
        var counter = new CountingTaskStore(new FileTaskStore(new FileTaskStoreOptions { RootPath = root }, clock));
        var index = new SqliteTaskIndex(Path.Combine(root, "index.db"), clock, TimeZoneInfo.Utc);
        var store = new IndexedTaskStore(counter, index);
        await store.InitializeAsync();

        var group = new TaskGroup { Name = "업무" };
        await store.SaveAsync(group);
        var tags = new List<Tag>();
        foreach (var name in new[] { "긴급", "검토", "대기" })
        {
            var tag = new Tag { Name = name };
            await store.SaveAsync(tag);
            tags.Add(tag);
        }

        for (var i = 0; i < ListSize; i++)
        {
            await store.SaveAsync(new TaskItem
            {
                Title = $"할 일 {i}",
                TaskGroupId = i % 2 == 0 ? group.Id : null,
                TagIds = { tags[i % tags.Count].Id },
                When = ScheduledWhen.On(ZonedDateTime.FromUtc(Now.AddDays(i % 10).AddHours(1), "UTC")),
            });
        }

        return (store, counter, group, tags);
    }

    /// <summary>Passes every call through to a real store while tallying how much of the record folder it
    /// touched. <see cref="RecordReads"/> counts by-id reads; <see cref="FolderScans"/> counts partition
    /// enumerations — the operation whose cost grows with the user's list.</summary>
    private sealed class CountingTaskStore(ITaskStore inner) : ITaskStore
    {
        private int _recordReads;
        private int _folderScans;

        public int RecordReads => Volatile.Read(ref _recordReads);
        public int FolderScans => Volatile.Read(ref _folderScans);

        public void Reset()
        {
            Volatile.Write(ref _recordReads, 0);
            Volatile.Write(ref _folderScans, 0);
        }

        public Task<IReadOnlyList<T>> GetAllAsync<T>(CancellationToken cancellationToken = default)
            where T : RecordBase
        {
            Interlocked.Increment(ref _folderScans);
            return inner.GetAllAsync<T>(cancellationToken);
        }

        public Task<T?> GetAsync<T>(Guid id, CancellationToken cancellationToken = default)
            where T : RecordBase
        {
            Interlocked.Increment(ref _recordReads);
            return inner.GetAsync<T>(id, cancellationToken);
        }

        public Task SaveAsync<T>(T record, CancellationToken cancellationToken = default) where T : RecordBase
            => inner.SaveAsync(record, cancellationToken);

        public Task DeleteAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : RecordBase
            => inner.DeleteAsync<T>(id, cancellationToken);
    }

    private sealed class NullToastPresenter : IToastPresenter
    {
        public void Schedule(ToastScheduleRequest request) { }
        public void CancelScheduled(string tag) { }
        public void RemoveFromHistory(string tag) { }
        public IReadOnlyList<string> GetScheduledTags() => [];
        public IReadOnlyList<string> GetHistoryTags() => [];
        public void Show(string title, string body, string? activationArguments = null) { }
    }

    private sealed class EnabledNotificationPreferences : INotificationPreferences
    {
        public bool NotificationsEnabled => true;
        public event EventHandler? NotificationsEnabledChanged { add { } remove { } }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
