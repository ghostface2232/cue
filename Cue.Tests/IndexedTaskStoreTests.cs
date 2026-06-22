using Cue.Domain;
using Cue.Storage;
using Cue.Storage.Index;

namespace Cue.Tests;

/// <summary>
/// Exercises the SQLite query index through <see cref="IndexedTaskStore"/>: that it is a pure cache
/// rebuilt from the files, that every list read is served from the index, that the time-axis views
/// are computed against the current day rather than stored, and that writes update file and index
/// together.
/// </summary>
public sealed class IndexedTaskStoreTests : IAsyncLifetime
{
    private readonly List<string> _roots = new();

    // A fixed reference "now": Monday 2026-06-22 12:00 UTC. Tests pin task dates relative to this.
    private static readonly DateTimeOffset Now = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

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

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "cue-index-tests", Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return root;
    }

    private Task<IndexedTaskStore> OpenAsync(string root, TimeProvider clock)
        => IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = root },
            clock,
            // Compute "today" in UTC so the reference day matches the clock's instant regardless of
            // the machine's local zone — keeps the date comparisons deterministic on any host.
            TimeZoneInfo.Utc);

    /// <summary>A zoned instant whose calendar day (in UTC) is <paramref name="day"/>.</summary>
    private static ZonedDateTime OnDayZoned(DateOnly day)
        => ZonedDateTime.FromUtc(new DateTimeOffset(day.Year, day.Month, day.Day, 9, 0, 0, TimeSpan.Zero), "UTC");

    /// <summary>An OnDate "When" whose calendar day (in UTC) is <paramref name="day"/>.</summary>
    private static ScheduledWhen OnDay(DateOnly day, bool evening = false)
        => ScheduledWhen.On(OnDayZoned(day), evening);

    private static DateOnly Today => DateOnly.FromDateTime(Now.UtcDateTime);

    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RebuildsFromFilesAlone_WhenIndexDatabaseIsDeleted()
    {
        var root = NewRoot();
        var clock = new MutableTimeProvider(Now);

        var project = Guid.NewGuid();
        var todayTask = new TaskItem { Title = "오늘 할 일", When = OnDay(Today), ProjectId = project };
        var doneTask = new TaskItem { Title = "끝낸 일", CompletedAt = Now.AddDays(-1) };

        await using (var store = await OpenAsync(root, clock))
        {
            await store.SaveAsync(todayTask);
            await store.SaveAsync(doneTask);
        }

        // Blow away the derived index — the files are the only truth left.
        var dbPath = Path.Combine(root, "index.db");
        Assert.True(File.Exists(dbPath));
        File.Delete(dbPath);

        await using (var reopened = await OpenAsync(root, clock))
        {
            // Startup rebuild reconstructs everything purely from the per-record files.
            Assert.True(File.Exists(dbPath), "re-opening must recreate the index database");

            var today = await reopened.GetTodayAsync();
            Assert.Equal(todayTask.Id, Assert.Single(today).Id);

            var inProject = await reopened.GetByProjectAsync(project);
            Assert.Equal(todayTask.Id, Assert.Single(inProject).Id);

            var logbook = await reopened.GetLogbookAsync();
            Assert.Equal(doneTask.Id, Assert.Single(logbook).Id);
        }
    }

    [Fact]
    public async Task ListQueries_AreServedFromIndex_NotFromFiles()
    {
        var root = NewRoot();
        var clock = new MutableTimeProvider(Now);
        await using var store = await OpenAsync(root, clock);

        var task = new TaskItem { Title = "색인에서만 읽힘", When = OnDay(Today) };
        await store.SaveAsync(task);

        // Physically remove the task's file. If the list query scanned the folder it would now be
        // empty; if it reads the index it still returns the row.
        var taskFile = Path.Combine(root, "tasks", task.Id + ".json");
        Assert.True(File.Exists(taskFile));
        File.Delete(taskFile);

        var today = await store.GetTodayAsync();
        Assert.Equal(task.Id, Assert.Single(today).Id);

        // And the file-backed read confirms the file really is gone — the list came from SQLite.
        Assert.Empty(await store.GetAllAsync<TaskItem>());
        Assert.Null(await store.GetAsync<TaskItem>(task.Id));
    }

    [Fact]
    public async Task OverdueTask_RollsForwardIntoToday()
    {
        var root = NewRoot();
        var clock = new MutableTimeProvider(Now);
        await using var store = await OpenAsync(root, clock);

        // Scheduled for yesterday, never completed. The pinned absolute date is in the past, so the
        // computed Today view must carry it forward rather than lose it.
        var overdue = new TaskItem { Title = "어제 못 한 일", When = OnDay(Today.AddDays(-1)) };
        await store.SaveAsync(overdue);

        var today = await store.GetTodayAsync();
        Assert.Contains(today, t => t.Id == overdue.Id);

        // It is genuinely overdue, not future — so it is not in Upcoming.
        var upcoming = await store.GetUpcomingAsync();
        Assert.DoesNotContain(upcoming, t => t.Id == overdue.Id);
    }

    [Fact]
    public async Task SoftDelete_RemovesTaskFromEveryActiveView()
    {
        var root = NewRoot();
        var clock = new MutableTimeProvider(Now);
        await using var store = await OpenAsync(root, clock);

        var project = Guid.NewGuid();
        var section = Guid.NewGuid();
        var label = Guid.NewGuid();
        var task = new TaskItem
        {
            Title = "삭제 대상",
            When = OnDay(Today),
            ProjectId = project,
            SectionId = section,
            LabelIds = { label },
        };
        await store.SaveAsync(task);

        // Present everywhere it should be before deletion.
        Assert.Contains(await store.GetTodayAsync(), t => t.Id == task.Id);
        Assert.Contains(await store.GetByProjectAsync(project), t => t.Id == task.Id);
        Assert.Contains(await store.GetBySectionAsync(section), t => t.Id == task.Id);
        Assert.Contains(await store.GetByLabelAsync(label), t => t.Id == task.Id);

        await store.DeleteAsync<TaskItem>(task.Id);

        // Gone from every active view (tombstones are excluded by default).
        Assert.DoesNotContain(await store.GetTodayAsync(), t => t.Id == task.Id);
        Assert.DoesNotContain(await store.GetByProjectAsync(project), t => t.Id == task.Id);
        Assert.DoesNotContain(await store.GetBySectionAsync(section), t => t.Id == task.Id);
        Assert.DoesNotContain(await store.GetByLabelAsync(label), t => t.Id == task.Id);
        Assert.Empty(await store.GetLogbookAsync()); // a tombstone is not "completed"

        // But the file remains as a tombstone — soft delete, not removal.
        Assert.True(File.Exists(Path.Combine(root, "tasks", task.Id + ".json")));
        var fromFile = await store.GetAsync<TaskItem>(task.Id);
        Assert.NotNull(fromFile);
        Assert.NotNull(fromFile!.DeletedAt);
    }

    [Fact]
    public async Task SaveAndDelete_UpdateFileAndIndexTogether()
    {
        var root = NewRoot();
        var clock = new MutableTimeProvider(Now);
        await using var store = await OpenAsync(root, clock);

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var task = new TaskItem { Title = "이동하는 일", When = OnDay(Today), ProjectId = first };

        // Save writes both: the file exists and the index query returns it.
        await store.SaveAsync(task);
        Assert.True(File.Exists(Path.Combine(root, "tasks", task.Id + ".json")));
        Assert.Contains(await store.GetByProjectAsync(first), t => t.Id == task.Id);

        // Re-saving an edit reflects into the index immediately — no stale row, no rebuild needed.
        task.ProjectId = second;
        await store.SaveAsync(task);
        Assert.DoesNotContain(await store.GetByProjectAsync(first), t => t.Id == task.Id);
        Assert.Contains(await store.GetByProjectAsync(second), t => t.Id == task.Id);

        // Delete updates both: file becomes a tombstone, index drops it from active views.
        await store.DeleteAsync<TaskItem>(task.Id);
        Assert.True(File.Exists(Path.Combine(root, "tasks", task.Id + ".json")));
        Assert.DoesNotContain(await store.GetByProjectAsync(second), t => t.Id == task.Id);
    }

    [Fact]
    public async Task TimeViews_AreComputedAgainstCurrentDay_NotStored()
    {
        var root = NewRoot();
        var clock = new MutableTimeProvider(Now);
        await using var store = await OpenAsync(root, clock);

        var dueDay = Today.AddDays(3); // 2026-06-25
        var task = new TaskItem { Title = "예정된 일", When = OnDay(dueDay) };
        await store.SaveAsync(task); // saved exactly once; never re-saved below

        // Before the day: it is Upcoming, not Today.
        Assert.Contains(await store.GetUpcomingAsync(), t => t.Id == task.Id);
        Assert.DoesNotContain(await store.GetTodayAsync(), t => t.Id == task.Id);

        // Advance the clock onto the day — with no re-save, the same row is now Today.
        clock.Now = new DateTimeOffset(dueDay.Year, dueDay.Month, dueDay.Day, 8, 0, 0, TimeSpan.Zero);
        Assert.Contains(await store.GetTodayAsync(), t => t.Id == task.Id);
        Assert.DoesNotContain(await store.GetUpcomingAsync(), t => t.Id == task.Id);

        // Advance past the day — still Today (overdue carry-forward), never back to Upcoming.
        clock.Now = clock.Now.AddDays(2);
        Assert.Contains(await store.GetTodayAsync(), t => t.Id == task.Id);
        Assert.DoesNotContain(await store.GetUpcomingAsync(), t => t.Id == task.Id);
    }

    [Fact]
    public async Task ClassificationAndTimeBuckets_PartitionTasksAsExpected()
    {
        var root = NewRoot();
        var clock = new MutableTimeProvider(Now);
        await using var store = await OpenAsync(root, clock);

        // Everything except `inbox` is filed under a project, so the classification axis (Inbox =
        // project-less) and the time axis stay cleanly separable in the assertions below.
        var filed = Guid.NewGuid();
        var inbox = new TaskItem { Title = "미분류", When = ScheduledWhen.Unscheduled };                            // Inbox + Anytime
        var someday = new TaskItem { Title = "언젠가", When = ScheduledWhen.SomeDay, ProjectId = filed };           // Someday
        var evening = new TaskItem { Title = "오늘 저녁", When = OnDay(Today, evening: true), ProjectId = filed };   // Today + This Evening
        var future = new TaskItem { Title = "다가오는 일", When = OnDay(Today.AddDays(2)), ProjectId = filed };      // Upcoming
        var deadlineOnly = new TaskItem { Title = "마감만 있음", ProjectId = filed, Deadline = ZonedDateTime.FromUtc(
            new DateTimeOffset(Today.AddDays(5).Year, Today.AddDays(5).Month, Today.AddDays(5).Day, 9, 0, 0, TimeSpan.Zero), "UTC") };
        var done = new TaskItem { Title = "완료", When = OnDay(Today), CompletedAt = Now, ProjectId = filed };      // Logbook only

        foreach (var t in new[] { inbox, someday, evening, future, deadlineOnly, done })
            await store.SaveAsync(t);

        // Inbox is exclusively the project-less task; Someday/This Evening are exclusive buckets.
        Assert.Equal(new[] { inbox.Id }, (await store.GetInboxAsync()).Select(t => t.Id));
        Assert.Equal(new[] { someday.Id }, (await store.GetSomedayAsync()).Select(t => t.Id));
        Assert.Equal(new[] { evening.Id }, (await store.GetThisEveningAsync()).Select(t => t.Id));

        // Anytime = open tasks with no scheduled When. The deadline-only task has no When, so it sits
        // in Anytime *and* (via its future deadline) in Upcoming — a legitimate cross-axis overlap.
        Assert.Equal(
            new[] { inbox.Id, deadlineOnly.Id }.OrderBy(g => g),
            (await store.GetAnytimeAsync()).Select(t => t.Id).OrderBy(g => g));

        var today = (await store.GetTodayAsync()).Select(t => t.Id).ToHashSet();
        Assert.Contains(evening.Id, today);
        Assert.DoesNotContain(done.Id, today);          // completed never shows in Today
        Assert.DoesNotContain(future.Id, today);        // future When
        Assert.DoesNotContain(deadlineOnly.Id, today);  // no When ⇒ not a Today item

        var upcoming = (await store.GetUpcomingAsync()).Select(t => t.Id).ToHashSet();
        Assert.Contains(future.Id, upcoming);        // future When
        Assert.Contains(deadlineOnly.Id, upcoming);  // future Deadline, no When

        Assert.Equal(new[] { done.Id }, (await store.GetLogbookAsync()).Select(t => t.Id));
    }

    [Fact]
    public async Task DeadlineDueOrOverdue_SurfacesInToday_NotUpcoming()
    {
        var root = NewRoot();
        var clock = new MutableTimeProvider(Now);
        await using var store = await OpenAsync(root, clock);

        // Deadline-only tasks (no scheduled When) at today and in the past. Before this fix they fell
        // through every time view — not Today (no When) and not Upcoming (deadline not in the future).
        var dueToday = new TaskItem { Title = "오늘 마감", Deadline = OnDayZoned(Today) };
        var overdue = new TaskItem { Title = "지난 마감", Deadline = OnDayZoned(Today.AddDays(-2)) };
        var future = new TaskItem { Title = "다가올 마감", Deadline = OnDayZoned(Today.AddDays(4)) };
        foreach (var t in new[] { dueToday, overdue, future })
            await store.SaveAsync(t);

        var today = (await store.GetTodayAsync()).Select(t => t.Id).ToHashSet();
        Assert.Contains(dueToday.Id, today);   // due today ⇒ Today
        Assert.Contains(overdue.Id, today);    // overdue ⇒ rolls forward into Today
        Assert.DoesNotContain(future.Id, today);

        var upcoming = (await store.GetUpcomingAsync()).Select(t => t.Id).ToHashSet();
        Assert.Contains(future.Id, upcoming);          // future deadline ⇒ Upcoming
        Assert.DoesNotContain(dueToday.Id, upcoming);  // due today is in Today, not Upcoming
        Assert.DoesNotContain(overdue.Id, upcoming);   // overdue is in Today, not Upcoming
    }

    [Fact]
    public async Task IndexDatabase_CanLiveOutsideTheDataRoot()
    {
        var dataRoot = NewRoot();
        var indexDir = NewRoot();
        var indexPath = Path.Combine(indexDir, "cache", "cue-index.db");
        var clock = new MutableTimeProvider(Now);

        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = dataRoot, IndexPath = indexPath },
            clock, TimeZoneInfo.Utc);

        var task = new TaskItem { Title = "분리된 색인", When = OnDay(Today) };
        await store.SaveAsync(task);

        // The index lives where we put it (a per-device location), not inside the data root — so when
        // the data root is a cloud folder, the index is never swept into sync.
        Assert.True(File.Exists(indexPath));
        Assert.False(File.Exists(Path.Combine(dataRoot, "index.db")));

        // And it still works: the list read is served from that out-of-root index.
        Assert.Contains(await store.GetTodayAsync(), t => t.Id == task.Id);
    }

    [Fact]
    public async Task DanglingReferences_AreToleratedByIndexAndViews()
    {
        var root = NewRoot();
        var clock = new MutableTimeProvider(Now);
        await using var store = await OpenAsync(root, clock);

        // The store enforces no referential integrity, so the index and views must tolerate floating
        // references: a project/section/label/parent that no record exists for.
        var missingProject = Guid.NewGuid();
        var missingSection = Guid.NewGuid();
        var missingLabel = Guid.NewGuid();
        var missingParent = Guid.NewGuid();
        var orphan = new TaskItem
        {
            Title = "떠 있는 참조",
            When = OnDay(Today),
            ProjectId = missingProject,
            SectionId = missingSection,
            ParentTaskId = missingParent,   // a sub-task whose parent never existed
            LabelIds = { missingLabel },
        };
        await store.SaveAsync(orphan);

        // Time views never resolve references, so the orphan surfaces normally.
        Assert.Contains(await store.GetTodayAsync(), t => t.Id == orphan.Id);
        // Classification queries by the dangling ids just return it — nothing joins or throws.
        Assert.Contains(await store.GetByProjectAsync(missingProject), t => t.Id == orphan.Id);
        Assert.Contains(await store.GetBySectionAsync(missingSection), t => t.Id == orphan.Id);
        Assert.Contains(await store.GetByLabelAsync(missingLabel), t => t.Id == orphan.Id);

        // A full rebuild from the files tolerates the same floating references.
        await store.InitializeAsync();
        Assert.Contains(await store.GetTodayAsync(), t => t.Id == orphan.Id);
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public MutableTimeProvider(DateTimeOffset start) => Now = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
