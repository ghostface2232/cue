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
    public async Task NavigationRecords_AreIndexedFilteredAndRebuiltFromFiles()
    {
        var root = NewRoot();
        var clock = new MutableTimeProvider(Now);
        var activeProject = new Project { Name = "활성 프로젝트", SortOrder = "a" };
        var archivedProject = new Project { Name = "보관 프로젝트", IsArchived = true };
        var completedProject = new Project { Name = "완료 프로젝트", CompletedAt = Now };
        var activeSection = new Section { ProjectId = activeProject.Id, Name = "활성 섹션" };
        var archivedSection = new Section { ProjectId = activeProject.Id, Name = "보관 섹션", IsArchived = true };
        var completedSection = new Section { ProjectId = activeProject.Id, Name = "완료 섹션", CompletedAt = Now };
        var label = new Label { Name = "중요", Color = "#ff0000" };

        await using (var store = await OpenAsync(root, clock))
        {
            foreach (var project in new[] { activeProject, archivedProject, completedProject })
                await store.SaveAsync(project);
            foreach (var section in new[] { activeSection, archivedSection, completedSection })
                await store.SaveAsync(section);
            await store.SaveAsync(label);

            Assert.Equal(activeProject.Id, Assert.Single(await store.GetProjectsAsync()).Id);
            Assert.Equal(activeSection.Id, Assert.Single(await store.GetSectionsByProjectAsync(activeProject.Id)).Id);
            Assert.Equal(label.Id, Assert.Single(await store.GetLabelsAsync()).Id);

            // An ordinary Save updates the file first and immediately reflects a rename into SQLite.
            activeProject.Name = "이름 변경됨";
            await store.SaveAsync(activeProject);
            Assert.Equal("이름 변경됨", Assert.Single(await store.GetProjectsAsync()).Name);
        }

        File.Delete(Path.Combine(root, "index.db"));
        await using var reopened = await OpenAsync(root, clock);
        Assert.Equal("이름 변경됨", Assert.Single(await reopened.GetProjectsAsync()).Name);
        Assert.Equal(activeSection.Id, Assert.Single(await reopened.GetSectionsByProjectAsync(activeProject.Id)).Id);
        Assert.Equal(label.Id, Assert.Single(await reopened.GetLabelsAsync()).Id);
    }

    [Fact]
    public async Task NavigationLists_AreServedFromIndex_NotRecordFolders()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root, new MutableTimeProvider(Now));
        var project = new Project { Name = "색인 프로젝트" };
        var label = new Label { Name = "색인 라벨" };
        await store.SaveAsync(project);
        await store.SaveAsync(label);

        File.Delete(Path.Combine(root, "projects", project.Id + ".json"));
        File.Delete(Path.Combine(root, "labels", label.Id + ".json"));

        Assert.Equal(project.Id, Assert.Single(await store.GetProjectsAsync()).Id);
        Assert.Equal(label.Id, Assert.Single(await store.GetLabelsAsync()).Id);
        Assert.Null(await store.GetAsync<Project>(project.Id));
        Assert.Null(await store.GetAsync<Label>(label.Id));
    }

    [Fact]
    public async Task DeleteProject_PreservesTasksByMovingThemToInbox_AndSoftDeletesSections()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root, new MutableTimeProvider(Now));
        var project = new Project { Name = "삭제할 프로젝트" };
        var section = new Section { ProjectId = project.Id, Name = "그 안의 섹션" };
        var open = new TaskItem { Title = "살려 둘 일", ProjectId = project.Id, SectionId = section.Id };
        var done = new TaskItem { Title = "완료한 일", ProjectId = project.Id, SectionId = section.Id, CompletedAt = Now };
        await store.SaveAsync(project);
        await store.SaveAsync(section);
        await store.SaveAsync(open);
        await store.SaveAsync(done);

        await store.DeleteAsync<Project>(project.Id);

        Assert.Null((await store.GetAsync<TaskItem>(open.Id))!.ProjectId);
        Assert.Null((await store.GetAsync<TaskItem>(open.Id))!.SectionId);
        Assert.Null((await store.GetAsync<TaskItem>(done.Id))!.ProjectId);
        Assert.Null((await store.GetAsync<TaskItem>(done.Id))!.SectionId);
        Assert.Equal(open.Id, Assert.Single(await store.GetInboxAsync()).Id);
        Assert.NotNull((await store.GetAsync<Project>(project.Id))!.DeletedAt);
        Assert.NotNull((await store.GetAsync<Section>(section.Id))!.DeletedAt);
        Assert.Empty(await store.GetProjectsAsync());
        Assert.Empty(await store.GetSectionsByProjectAsync(project.Id));
    }

    [Fact]
    public async Task DeleteSection_PreservesTasksByMovingThemToInbox()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root, new MutableTimeProvider(Now));
        var project = new Project { Name = "남는 프로젝트" };
        var section = new Section { ProjectId = project.Id, Name = "삭제할 섹션" };
        var task = new TaskItem { Title = "Inbox로 이동", ProjectId = project.Id, SectionId = section.Id };
        await store.SaveAsync(project);
        await store.SaveAsync(section);
        await store.SaveAsync(task);

        await store.DeleteAsync<Section>(section.Id);

        var moved = await store.GetAsync<TaskItem>(task.Id);
        Assert.Null(moved!.ProjectId);
        Assert.Null(moved.SectionId);
        Assert.Equal(task.Id, Assert.Single(await store.GetInboxAsync()).Id);
        Assert.NotNull((await store.GetAsync<Section>(section.Id))!.DeletedAt);
        Assert.Equal(project.Id, Assert.Single(await store.GetProjectsAsync()).Id);
    }

    [Fact]
    public async Task DeleteLabel_RemovesOnlyReferences_AndNeverDeletesTasks()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root, new MutableTimeProvider(Now));
        var removed = new Label { Name = "지울 라벨" };
        var kept = new Label { Name = "남길 라벨" };
        var task = new TaskItem { Title = "그대로 남는 일", LabelIds = { removed.Id, kept.Id } };
        await store.SaveAsync(removed);
        await store.SaveAsync(kept);
        await store.SaveAsync(task);

        await store.DeleteAsync<Label>(removed.Id);

        var preserved = await store.GetAsync<TaskItem>(task.Id);
        Assert.NotNull(preserved);
        Assert.Equal(new[] { kept.Id }, preserved!.LabelIds);
        Assert.Empty(await store.GetByLabelAsync(removed.Id));
        Assert.Equal(task.Id, Assert.Single(await store.GetByLabelAsync(kept.Id)).Id);
        Assert.NotNull((await store.GetAsync<Label>(removed.Id))!.DeletedAt);
        Assert.Equal(kept.Id, Assert.Single(await store.GetLabelsAsync()).Id);
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

    [Fact]
    public async Task SubtaskList_ComesFromIndex_IncludesCompleted_AndExcludesTombstones()
    {
        var root = NewRoot();
        var clock = new MutableTimeProvider(Now);
        await using var store = await OpenAsync(root, clock);
        var parent = new TaskItem { Title = "부모" };
        var open = new TaskItem { Title = "열린 하위 작업", ParentTaskId = parent.Id };
        var completed = new TaskItem { Title = "완료한 하위 작업", ParentTaskId = parent.Id, CompletedAt = Now };
        var other = new TaskItem { Title = "다른 부모의 작업", ParentTaskId = Guid.NewGuid() };
        await store.SaveAsync(parent);
        await store.SaveAsync(open);
        await store.SaveAsync(completed);
        await store.SaveAsync(other);
        await store.DeleteAsync<TaskItem>(other.Id);

        Assert.Equal(
            new[] { open.Id, completed.Id }.OrderBy(id => id),
            (await store.GetSubtasksAsync(parent.Id)).Select(item => item.Id).OrderBy(id => id));

        // Prove this list is read from SQLite, not by scanning task files.
        File.Delete(Path.Combine(root, "tasks", open.Id + ".json"));
        Assert.Contains(await store.GetSubtasksAsync(parent.Id), item => item.Id == open.Id);
    }

    [Fact]
    public async Task DetailedTaskEdit_IsImmediatelyReflectedAcrossClassificationAndTimeViews()
    {
        var root = NewRoot();
        var clock = new MutableTimeProvider(Now);
        await using var store = await OpenAsync(root, clock);
        var project = new Project { Name = "옮길 프로젝트" };
        var label = new Label { Name = "붙일 라벨" };
        var task = new TaskItem { Title = "편집 전" };
        await store.SaveAsync(project);
        await store.SaveAsync(label);
        await store.SaveAsync(task);
        Assert.Equal(task.Id, Assert.Single(await store.GetInboxAsync()).Id);

        task.Title = "편집 후";
        task.Priority = Priority.P1;
        task.ProjectId = project.Id;
        task.LabelIds.Add(label.Id);
        task.When = OnDay(Today.AddDays(3), evening: true);
        task.Deadline = OnDayZoned(Today.AddDays(4));
        await store.SaveAsync(task);

        Assert.Empty(await store.GetInboxAsync());
        Assert.Equal(task.Id, Assert.Single(await store.GetByProjectAsync(project.Id)).Id);
        Assert.Equal(task.Id, Assert.Single(await store.GetByLabelAsync(label.Id)).Id);
        var indexed = Assert.Single(await store.GetUpcomingAsync(), item => item.Id == task.Id);
        Assert.Equal("편집 후", indexed.Title);
        Assert.Equal(Priority.P1, indexed.Priority);
        Assert.Equal(Today.AddDays(3), indexed.WhenDate);
        Assert.True(indexed.IsEvening);
        Assert.Equal(Today.AddDays(4), indexed.DeadlineDate);
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public MutableTimeProvider(DateTimeOffset start) => Now = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
