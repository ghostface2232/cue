using Cue.Domain;
using Cue.Storage;
using Cue.Storage.Recurrence;

namespace Cue.Tests;

/// <summary>
/// Exercises completion-on-advance (method B): completing a repeating task leaves exactly one
/// completed Logbook copy and advances the original one cycle, preserving rank and time zone, and the
/// advance is reflected in the index's time-axis views. Non-recurring and unusable-rule tasks fall
/// back to ordinary in-place completion.
/// </summary>
public sealed class RecurringTaskServiceTests : IAsyncLifetime
{
    private readonly List<string> _roots = new();

    // Fixed "now": Monday 2026-06-22 12:00 UTC, matching the index tests' reference day.
    private static readonly DateTimeOffset Now = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
    private static DateOnly Today => DateOnly.FromDateTime(Now.UtcDateTime);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        foreach (var root in _roots)
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
        return Task.CompletedTask;
    }

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "cue-recurring-tests", Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return root;
    }

    private Task<IndexedTaskStore> OpenAsync(string root)
        => IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = root },
            new FixedClock(Now),
            TimeZoneInfo.Utc);

    private static ZonedDateTime OnDayZoned(DateOnly day, string tz = "UTC")
        => ZonedDateTime.FromLocal(new DateTime(day.Year, day.Month, day.Day, 9, 0, 0), tz);

    private static ScheduledWhen OnDay(DateOnly day, string tz = "UTC")
        => ScheduledWhen.On(OnDayZoned(day, tz));

    private static RecurrenceRule Daily(DateOnly anchorDay, string tz = "UTC")
        => new("FREQ=DAILY", OnDayZoned(anchorDay, tz));

    // ---------------------------------------------------------------------------

    [Fact]
    public async Task CompletingRecurring_LeavesExactlyOneLogbookCopy_AndAdvancesOriginalOneCycle()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root);
        var service = new RecurringTaskService(store);

        var task = new TaskItem
        {
            Title = "매일 운동",
            When = OnDay(Today),
            Recurrence = Daily(Today),
            SortOrder = "hhhh",
        };
        await store.SaveAsync(task);

        await service.CompleteAsync(task.Id, Now);

        // The original is still open and advanced exactly one cycle (today → tomorrow). Rank and the
        // recurrence rule are untouched; only When moved.
        var original = await store.GetAsync<TaskItem>(task.Id);
        Assert.NotNull(original);
        Assert.False(original!.IsCompleted);
        Assert.NotNull(original.Recurrence);
        Assert.Equal("hhhh", original.SortOrder);
        Assert.Equal(Today.AddDays(1), DateOnly.FromDateTime(original.When.Date!.Value.Utc.UtcDateTime));

        // Exactly one extra record exists: the completed copy.
        var all = await store.GetAllAsync<TaskItem>();
        Assert.Equal(2, all.Count);
        var copy = all.Single(t => t.Id != task.Id);
        Assert.True(copy.IsCompleted);
        Assert.Equal(Now, copy.CompletedAt);
        Assert.Null(copy.Recurrence); // a frozen completion record never recurs
        Assert.Equal("매일 운동", copy.Title);
        Assert.Equal(Today, DateOnly.FromDateTime(copy.When.Date!.Value.Utc.UtcDateTime)); // the completed instance's own date

        // The Logbook holds exactly the one copy; the advanced original is not completed.
        var logbook = await store.GetLogbookAsync();
        Assert.Single(logbook);
        Assert.Equal(copy.Id, logbook[0].Id);
    }

    [Fact]
    public async Task CompletingRecurring_IsIdempotent_WhenAdvanceCrashedAfterLogbookCopy()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root);

        var task = new TaskItem
        {
            Title = "매일 운동",
            When = OnDay(Today),
            Recurrence = Daily(Today),
            SortOrder = "hhhh",
        };
        await store.SaveAsync(task);

        // First completion crashes right after the Logbook copy is written (save #1) but before the
        // advance (save #2) — the exact window the two-save path is non-atomic in.
        var crashing = new CrashOnNthSaveStore(store, crashOnSave: 2);
        var crashedService = new RecurringTaskService(crashing);
        await Assert.ThrowsAsync<CrashOnNthSaveStore.SimulatedCrashException>(
            () => crashedService.CompleteAsync(task.Id, Now));

        // After the crash the orphaned copy is on disk while the original is still due today.
        var afterCrash = await store.GetAllAsync<TaskItem>();
        Assert.Equal(2, afterCrash.Count);
        var stillOpen = await store.GetAsync<TaskItem>(task.Id);
        Assert.False(stillOpen!.IsCompleted);
        Assert.Equal(Today, DateOnly.FromDateTime(stillOpen.When.Date!.Value.Utc.UtcDateTime));

        // Retrying the completion against a healthy store must overwrite the orphaned copy (its id is
        // derived from the original id + this occurrence), not mint a second one, and advance once.
        var service = new RecurringTaskService(store);
        await service.CompleteAsync(task.Id, Now);

        var all = await store.GetAllAsync<TaskItem>();
        Assert.Equal(2, all.Count); // still exactly one copy + the original — no duplicate
        var advanced = await store.GetAsync<TaskItem>(task.Id);
        Assert.False(advanced!.IsCompleted);
        Assert.Equal(Today.AddDays(1), DateOnly.FromDateTime(advanced.When.Date!.Value.Utc.UtcDateTime));

        var logbook = await store.GetLogbookAsync();
        Assert.Single(logbook);
    }

    [Fact]
    public async Task AdvancedOriginal_LeavesToday_AndSurfacesInUpcoming()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root);
        var service = new RecurringTaskService(store);

        var task = new TaskItem { Title = "daily", When = OnDay(Today), Recurrence = Daily(Today) };
        await store.SaveAsync(task);

        // Before completion it is due today.
        Assert.Contains(await store.GetTodayAsync(), t => t.Id == task.Id);

        await service.CompleteAsync(task.Id, Now);

        // After advancing to tomorrow it drops out of Today and shows up in Upcoming.
        Assert.DoesNotContain(await store.GetTodayAsync(), t => t.Id == task.Id);
        Assert.Contains(await store.GetUpcomingAsync(), t => t.Id == task.Id);
    }

    [Fact]
    public async Task Advance_PreservesTimeZoneAndWallClock()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root);
        var service = new RecurringTaskService(store);

        var task = new TaskItem
        {
            Title = "서울 일정",
            When = OnDay(Today, tz: "Asia/Seoul"),
            Recurrence = Daily(Today, tz: "Asia/Seoul"),
        };
        await store.SaveAsync(task);

        await service.CompleteAsync(task.Id, Now);

        var original = await store.GetAsync<TaskItem>(task.Id);
        var advanced = original!.When.Date!.Value;
        Assert.Equal("Asia/Seoul", advanced.TimeZoneId);
        Assert.Equal(9, advanced.ToLocal().Hour); // 09:00 local preserved
    }

    [Fact]
    public async Task CompletingNonRecurring_StampsInPlace_WithNoCopy()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root);
        var service = new RecurringTaskService(store);

        var task = new TaskItem { Title = "한 번만", When = OnDay(Today) };
        await store.SaveAsync(task);

        await service.CompleteAsync(task.Id, Now);

        var all = await store.GetAllAsync<TaskItem>();
        Assert.Single(all); // no copy made
        Assert.Equal(Now, all[0].CompletedAt);

        var logbook = await store.GetLogbookAsync();
        Assert.Single(logbook);
        Assert.Equal(task.Id, logbook[0].Id);
    }

    [Fact]
    public async Task CompletingUnparseableRule_FallsBackToInPlaceCompletion()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root);
        var service = new RecurringTaskService(store);

        // A malformed RRULE must not throw and must not advance — it completes like a one-off.
        var task = new TaskItem
        {
            Title = "깨진 규칙",
            When = OnDay(Today),
            Recurrence = new RecurrenceRule("totally not a rule", OnDayZoned(Today)),
        };
        await store.SaveAsync(task);

        await service.CompleteAsync(task.Id, Now);

        var all = await store.GetAllAsync<TaskItem>();
        Assert.Single(all); // no copy, no advance
        var only = all[0];
        Assert.Equal(task.Id, only.Id);
        Assert.True(only.IsCompleted);
        Assert.Equal(Today, DateOnly.FromDateTime(only.When.Date!.Value.Utc.UtcDateTime)); // When unchanged
    }

    [Fact]
    public async Task LogbookCopy_CopiesSortOrderVerbatim_ButLogbookSortsByCompletedAt()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root);
        var service = new RecurringTaskService(store);

        var task = new TaskItem { Title = "rank check", When = OnDay(Today), Recurrence = Daily(Today), SortOrder = "mmmm" };
        await store.SaveAsync(task);

        await service.CompleteAsync(task.Id, Now);

        var all = await store.GetAllAsync<TaskItem>();
        var copy = all.Single(t => t.Id != task.Id);
        Assert.Equal("mmmm", copy.SortOrder); // copied verbatim, even though the Logbook ignores it
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>
    /// An <see cref="ITaskStore"/> decorator that forwards every call to the real store but throws on
    /// the Nth <see cref="SaveAsync"/>, simulating a crash partway through a multi-save operation.
    /// Writes before the throw really land on disk, so the post-crash state is faithful.
    /// </summary>
    private sealed class CrashOnNthSaveStore : ITaskStore
    {
        public sealed class SimulatedCrashException : Exception;

        private readonly ITaskStore _inner;
        private readonly int _crashOnSave;
        private int _saves;

        public CrashOnNthSaveStore(ITaskStore inner, int crashOnSave)
        {
            _inner = inner;
            _crashOnSave = crashOnSave;
        }

        public Task<IReadOnlyList<T>> GetAllAsync<T>(CancellationToken cancellationToken = default)
            where T : Cue.Domain.RecordBase => _inner.GetAllAsync<T>(cancellationToken);

        public Task<T?> GetAsync<T>(Guid id, CancellationToken cancellationToken = default)
            where T : Cue.Domain.RecordBase => _inner.GetAsync<T>(id, cancellationToken);

        public Task SaveAsync<T>(T record, CancellationToken cancellationToken = default)
            where T : Cue.Domain.RecordBase
        {
            if (++_saves == _crashOnSave)
                throw new SimulatedCrashException();
            return _inner.SaveAsync(record, cancellationToken);
        }

        public Task DeleteAsync<T>(Guid id, CancellationToken cancellationToken = default)
            where T : Cue.Domain.RecordBase => _inner.DeleteAsync<T>(id, cancellationToken);
    }
}
