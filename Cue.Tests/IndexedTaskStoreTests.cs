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
    private static ScheduledWhen OnDay(DateOnly day)
        => ScheduledWhen.On(OnDayZoned(day));

    private static DateOnly Today => DateOnly.FromDateTime(Now.UtcDateTime);

    [Fact]
    public async Task RebuildsFromFilesAlone_WhenIndexDatabaseIsDeleted()
    {
        var root = NewRoot();
        var clock = new MutableTimeProvider(Now);

        var project = Guid.NewGuid();
        var todayTask = new TaskItem { Title = "오늘 할 일", When = OnDay(Today), TaskGroupId = project };
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

            var inGroup = await reopened.GetByTaskGroupAsync(project);
            Assert.Equal(todayTask.Id, Assert.Single(inGroup).Id);

            var logbook = await reopened.GetLogbookAsync();
            Assert.Equal(doneTask.Id, Assert.Single(logbook).Id);
        }
    }

    [Fact]
    public async Task NavigationRecords_AreIndexedFilteredAndRebuiltFromFiles()
    {
        var root = NewRoot();
        var clock = new MutableTimeProvider(Now);
        var activeGroup = new TaskGroup { Name = "활성 프로젝트", SortOrder = "a" };
        var deletedGroup = new TaskGroup { Name = "삭제된 프로젝트" };
        var label = new Tag { Name = "중요", Color = "#ff0000" };

        await using (var store = await OpenAsync(root, clock))
        {
            await store.SaveAsync(activeGroup);
            await store.SaveAsync(deletedGroup);
            await store.SaveAsync(label);
            await store.DeleteAsync<TaskGroup>(deletedGroup.Id);

            // Active (non-tombstoned) projects only; a pure group has no archived/completed state.
            Assert.Equal(activeGroup.Id, Assert.Single(await store.GetTaskGroupsAsync()).Id);
            Assert.Equal(label.Id, Assert.Single(await store.GetTagsAsync()).Id);

            // An ordinary Save updates the file first and immediately reflects a rename into SQLite.
            activeGroup.Name = "이름 변경됨";
            await store.SaveAsync(activeGroup);
            Assert.Equal("이름 변경됨", Assert.Single(await store.GetTaskGroupsAsync()).Name);
        }

        File.Delete(Path.Combine(root, "index.db"));
        await using var reopened = await OpenAsync(root, clock);
        Assert.Equal("이름 변경됨", Assert.Single(await reopened.GetTaskGroupsAsync()).Name);
        Assert.Equal(label.Id, Assert.Single(await reopened.GetTagsAsync()).Id);
    }

    [Fact]
    public async Task NavigationLists_AreServedFromIndex_NotRecordFolders()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root, new MutableTimeProvider(Now));
        var project = new TaskGroup { Name = "색인 프로젝트" };
        var label = new Tag { Name = "색인 라벨" };
        await store.SaveAsync(project);
        await store.SaveAsync(label);

        File.Delete(Path.Combine(root, "groups", project.Id + ".json"));
        File.Delete(Path.Combine(root, "tags", label.Id + ".json"));

        Assert.Equal(project.Id, Assert.Single(await store.GetTaskGroupsAsync()).Id);
        Assert.Equal(label.Id, Assert.Single(await store.GetTagsAsync()).Id);
        Assert.Null(await store.GetAsync<TaskGroup>(project.Id));
        Assert.Null(await store.GetAsync<Tag>(label.Id));
    }

    [Fact]
    public async Task UnfiledLists_GatherTasksWithNoGroupOrNoTag()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root, new MutableTimeProvider(Now));
        var project = new TaskGroup { Name = "그룹" };
        var label = new Tag { Name = "태그" };
        await store.SaveAsync(project);
        await store.SaveAsync(label);

        // unfiled: no group, no label — the quick-capture leftover the 그룹 없음/태그 없음 tabs re-gather.
        // Explicit sort orders keep the list order deterministic; the completed task sorts last despite
        // its early rank, proving the completed-last ordering.
        var unfiled = new TaskItem { Title = "미분류", SortOrder = "b" };
        var grouped = new TaskItem { Title = "그룹에 든 일", TaskGroupId = project.Id, SortOrder = "c" };
        var tagged = new TaskItem { Title = "태그 붙은 일", TagIds = { label.Id }, SortOrder = "d" };
        var doneUnfiled = new TaskItem { Title = "끝낸 미분류", CompletedAt = Now, SortOrder = "a" };
        await store.SaveAsync(unfiled);
        await store.SaveAsync(grouped);
        await store.SaveAsync(tagged);
        await store.SaveAsync(doneUnfiled);

        // 그룹 없음: every task with no group (tagged-but-ungrouped included), completed kept but dimmed below.
        Assert.Equal(
            new[] { unfiled.Id, tagged.Id, doneUnfiled.Id },
            (await store.GetWithoutTaskGroupAsync()).Select(t => t.Id));
        // 태그 없음: every task carrying no label (grouped-but-untagged included).
        Assert.Equal(
            new[] { unfiled.Id, grouped.Id, doneUnfiled.Id },
            (await store.GetWithoutTagAsync()).Select(t => t.Id));

        // Badge counts are open-only — the completed unfiled task drops out of both.
        Assert.Equal(2, await store.GetOpenTaskCountWithoutTaskGroupAsync());
        Assert.Equal(2, await store.GetOpenTaskCountWithoutTagAsync());

        // Filing the task away (give it a group and a tag) removes it from both unfiled lists.
        unfiled.TaskGroupId = project.Id;
        unfiled.TagIds.Add(label.Id);
        await store.SaveAsync(unfiled);
        Assert.DoesNotContain(await store.GetWithoutTaskGroupAsync(), t => t.Id == unfiled.Id);
        Assert.DoesNotContain(await store.GetWithoutTagAsync(), t => t.Id == unfiled.Id);
        Assert.Equal(1, await store.GetOpenTaskCountWithoutTaskGroupAsync());
        Assert.Equal(1, await store.GetOpenTaskCountWithoutTagAsync());
    }

    [Fact]
    public async Task DeleteTaskGroup_PreservesTasksByMovingThemToInbox()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root, new MutableTimeProvider(Now));
        var project = new TaskGroup { Name = "삭제할 프로젝트" };
        var open = new TaskItem { Title = "살려 둘 일", TaskGroupId = project.Id };
        var done = new TaskItem { Title = "완료한 일", TaskGroupId = project.Id, CompletedAt = Now };
        await store.SaveAsync(project);
        await store.SaveAsync(open);
        await store.SaveAsync(done);

        await store.DeleteAsync<TaskGroup>(project.Id);

        // Deleting a group never deletes its work; the tasks are ungrouped (TaskGroupId cleared).
        Assert.Null((await store.GetAsync<TaskItem>(open.Id))!.TaskGroupId);
        Assert.Null((await store.GetAsync<TaskItem>(done.Id))!.TaskGroupId);
        // Both stay in the home "모든 할 일" view; the completed one sinks (dimmed) below the open one.
        Assert.Equal(new[] { open.Id, done.Id }, (await store.GetAllActiveAsync()).Select(t => t.Id));
        Assert.NotNull((await store.GetAsync<TaskGroup>(project.Id))!.DeletedAt);
        Assert.Empty(await store.GetTaskGroupsAsync());
    }

    [Fact]
    public async Task DeleteTaskGroup_DeleteTasksMode_SoftDeletesEveryContainedTask()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root, new MutableTimeProvider(Now));
        var project = new TaskGroup { Name = "통째로 삭제할 프로젝트" };
        var open = new TaskItem { Title = "열린 일", TaskGroupId = project.Id };
        var done = new TaskItem { Title = "완료한 일", TaskGroupId = project.Id, CompletedAt = Now };
        var third = new TaskItem { Title = "또 다른 일", TaskGroupId = project.Id };
        var other = new TaskItem { Title = "다른 곳의 일" };
        await store.SaveAsync(project);
        await store.SaveAsync(open);
        await store.SaveAsync(done);
        await store.SaveAsync(third);
        await store.SaveAsync(other);

        // Opt-in destructive deletion: the group and all its tasks (open and completed) are tombstoned.
        await store.DeleteTaskGroupAsync(project.Id, TaskGroupDeletionMode.DeleteTasks);

        Assert.NotNull((await store.GetAsync<TaskItem>(open.Id))!.DeletedAt);
        Assert.NotNull((await store.GetAsync<TaskItem>(done.Id))!.DeletedAt);
        Assert.NotNull((await store.GetAsync<TaskItem>(third.Id))!.DeletedAt);
        Assert.NotNull((await store.GetAsync<TaskGroup>(project.Id))!.DeletedAt);
        Assert.Empty(await store.GetByTaskGroupAsync(project.Id));
        // A task that was never in the group is untouched and stays in the home "모든 할 일" view.
        Assert.Null((await store.GetAsync<TaskItem>(other.Id))!.DeletedAt);
        Assert.Equal(other.Id, Assert.Single(await store.GetAllActiveAsync()).Id);
    }

    [Fact]
    public async Task DeleteTaskGroup_ReparentMode_MovesTasksToInbox()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root, new MutableTimeProvider(Now));
        var project = new TaskGroup { Name = "유지할 프로젝트" };
        var task = new TaskItem { Title = "살려 둘 일", TaskGroupId = project.Id };
        await store.SaveAsync(project);
        await store.SaveAsync(task);

        // Reparent mode matches the generic DeleteAsync<TaskGroup> default: tasks survive, group goes.
        await store.DeleteTaskGroupAsync(project.Id, TaskGroupDeletionMode.Reparent);

        Assert.Null((await store.GetAsync<TaskItem>(task.Id))!.TaskGroupId);
        Assert.Null((await store.GetAsync<TaskItem>(task.Id))!.DeletedAt);
        Assert.Equal(task.Id, Assert.Single(await store.GetAllActiveAsync()).Id);
        Assert.NotNull((await store.GetAsync<TaskGroup>(project.Id))!.DeletedAt);
    }

    [Fact]
    public async Task DeleteTask_IsAPlainSoftDelete_WithItsEmbeddedChecklist()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root, new MutableTimeProvider(Now));
        var task = new TaskItem { Title = "부모" };
        task.Checklist.Add(new ChecklistItem { Title = "항목" });
        var unrelated = new TaskItem { Title = "남" };
        await store.SaveAsync(task);
        await store.SaveAsync(unrelated);

        // Deleting a task tombstones just that task; its embedded checklist goes with the record.
        await store.DeleteAsync<TaskItem>(task.Id);

        var deleted = await store.GetAsync<TaskItem>(task.Id);
        Assert.NotNull(deleted!.DeletedAt);
        Assert.Single(deleted.Checklist);
        Assert.DoesNotContain(await store.GetAllActiveAsync(), item => item.Id == task.Id);
        // An unrelated task is left alone.
        Assert.Null((await store.GetAsync<TaskItem>(unrelated.Id))!.DeletedAt);
    }

    [Fact]
    public async Task DeleteTag_RemovesOnlyReferences_AndNeverDeletesTasks()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root, new MutableTimeProvider(Now));
        var removed = new Tag { Name = "지울 라벨" };
        var kept = new Tag { Name = "남길 라벨" };
        var task = new TaskItem { Title = "그대로 남는 일", TagIds = { removed.Id, kept.Id } };
        await store.SaveAsync(removed);
        await store.SaveAsync(kept);
        await store.SaveAsync(task);

        await store.DeleteAsync<Tag>(removed.Id);

        var preserved = await store.GetAsync<TaskItem>(task.Id);
        Assert.NotNull(preserved);
        Assert.Equal(new[] { kept.Id }, preserved!.TagIds);
        Assert.Empty(await store.GetByTagAsync(removed.Id));
        Assert.Equal(task.Id, Assert.Single(await store.GetByTagAsync(kept.Id)).Id);
        Assert.NotNull((await store.GetAsync<Tag>(removed.Id))!.DeletedAt);
        Assert.Equal(kept.Id, Assert.Single(await store.GetTagsAsync()).Id);
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
        var label = Guid.NewGuid();
        var task = new TaskItem
        {
            Title = "삭제 대상",
            When = OnDay(Today),
            TaskGroupId = project,
            TagIds = { label },
        };
        await store.SaveAsync(task);

        // Present everywhere it should be before deletion.
        Assert.Contains(await store.GetTodayAsync(), t => t.Id == task.Id);
        Assert.Contains(await store.GetByTaskGroupAsync(project), t => t.Id == task.Id);
        Assert.Contains(await store.GetByTagAsync(label), t => t.Id == task.Id);

        await store.DeleteAsync<TaskItem>(task.Id);

        // Gone from every active view (tombstones are excluded by default).
        Assert.DoesNotContain(await store.GetTodayAsync(), t => t.Id == task.Id);
        Assert.DoesNotContain(await store.GetByTaskGroupAsync(project), t => t.Id == task.Id);
        Assert.DoesNotContain(await store.GetByTagAsync(label), t => t.Id == task.Id);
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
        var task = new TaskItem { Title = "이동하는 일", When = OnDay(Today), TaskGroupId = first };

        // Save writes both: the file exists and the index query returns it.
        await store.SaveAsync(task);
        Assert.True(File.Exists(Path.Combine(root, "tasks", task.Id + ".json")));
        Assert.Contains(await store.GetByTaskGroupAsync(first), t => t.Id == task.Id);

        // Re-saving an edit reflects into the index immediately — no stale row, no rebuild needed.
        task.TaskGroupId = second;
        await store.SaveAsync(task);
        Assert.DoesNotContain(await store.GetByTaskGroupAsync(first), t => t.Id == task.Id);
        Assert.Contains(await store.GetByTaskGroupAsync(second), t => t.Id == task.Id);

        // Delete updates both: file becomes a tombstone, index drops it from active views.
        await store.DeleteAsync<TaskItem>(task.Id);
        Assert.True(File.Exists(Path.Combine(root, "tasks", task.Id + ".json")));
        Assert.DoesNotContain(await store.GetByTaskGroupAsync(second), t => t.Id == task.Id);
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

        // Everything except `inbox`/`anytime` is filed under a project, so the classification axis
        // (Inbox = project-less) and the time axis stay cleanly separable in the assertions below.
        var filed = Guid.NewGuid();
        var inbox = new TaskItem { Title = "미분류", When = ScheduledWhen.Unscheduled };                            // Inbox + Anytime
        var anytimeFiled = new TaskItem { Title = "언젠가", When = ScheduledWhen.Unscheduled, TaskGroupId = filed };  // Anytime (filed)
        var todayTask = new TaskItem { Title = "오늘 할 일", When = OnDay(Today), TaskGroupId = filed };              // Today
        var future = new TaskItem { Title = "다가오는 일", When = OnDay(Today.AddDays(2)), TaskGroupId = filed };      // Upcoming
        var done = new TaskItem { Title = "완료", When = OnDay(Today), CompletedAt = Now, TaskGroupId = filed };      // Logbook only

        foreach (var t in new[] { inbox, anytimeFiled, todayTask, future, done })
            await store.SaveAsync(t);

        // The home "모든 할 일" (All) spans every group: it returns every non-deleted task, grouped or not.
        Assert.Equal(
            new[] { inbox.Id, anytimeFiled.Id, todayTask.Id, future.Id, done.Id }.OrderBy(g => g),
            (await store.GetAllActiveAsync()).Select(t => t.Id).OrderBy(g => g));

        // Anytime ("언젠가") = non-deleted tasks with no When date, regardless of project.
        Assert.Equal(
            new[] { inbox.Id, anytimeFiled.Id }.OrderBy(g => g),
            (await store.GetAnytimeAsync()).Select(t => t.Id).OrderBy(g => g));

        var today = (await store.GetTodayAsync()).Select(t => t.Id).ToHashSet();
        Assert.Contains(todayTask.Id, today);
        Assert.Contains(done.Id, today);                // completed stays in its time bucket (dimmed)
        Assert.DoesNotContain(future.Id, today);        // future When

        var upcoming = (await store.GetUpcomingAsync()).Select(t => t.Id).ToHashSet();
        Assert.Contains(future.Id, upcoming);        // future When

        Assert.Equal(new[] { done.Id }, (await store.GetLogbookAsync()).Select(t => t.Id));
    }

    [Fact]
    public async Task GetByPriority_ReturnsFlaggedTasksOrderedByPriority_OpenBeforeCompleted()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root, new MutableTimeProvider(Now));
        var p2 = new TaskItem { Title = "중요", Priority = Priority.P2 };
        var p1 = new TaskItem { Title = "매우 중요", Priority = Priority.P1 };
        var none = new TaskItem { Title = "중요도 없음" };
        var p1done = new TaskItem { Title = "완료한 P1", Priority = Priority.P1, CompletedAt = Now };
        foreach (var t in new[] { p2, p1, none, p1done })
            await store.SaveAsync(t);

        var ids = (await store.GetByPriorityAsync()).Select(t => t.Id).ToList();

        // Unflagged tasks are excluded; results run P1 → P4, with completed sinking within each level.
        Assert.DoesNotContain(none.Id, ids);
        Assert.Equal(new[] { p1.Id, p1done.Id, p2.Id }, ids);
    }

    [Fact]
    public async Task TimelineQuery_ReturnsDatedTasksWhoseWhenFallsInTheVisibleRange()
    {
        var root = NewRoot();
        await using var store = await OpenAsync(root, new MutableTimeProvider(Now));
        var rangeStart = new DateOnly(2026, 6, 1);
        var rangeEnd = new DateOnly(2026, 6, 30);

        var insideRange = new TaskItem
        {
            Title = "범위 안 일정",
            When = OnDay(new DateOnly(2026, 6, 12)),
            Priority = Priority.P2,
        };
        var onStartEdge = new TaskItem { Title = "시작 경계", When = OnDay(rangeStart) };
        var beforeRange = new TaskItem { Title = "범위 전", When = OnDay(new DateOnly(2026, 5, 29)) };
        var afterRange = new TaskItem { Title = "범위 밖", When = OnDay(new DateOnly(2026, 7, 2)) };
        var dateless = new TaskItem { Title = "날짜 없음" };

        foreach (var task in new[] { insideRange, onStartEdge, beforeRange, afterRange, dateless })
            await store.SaveAsync(task);

        var timeline = await store.GetTimelineAsync(rangeStart, rangeEnd);
        var ids = timeline.Select(item => item.Id).ToHashSet();

        Assert.Contains(insideRange.Id, ids);
        Assert.Contains(onStartEdge.Id, ids);
        Assert.DoesNotContain(beforeRange.Id, ids);   // When before the range
        Assert.DoesNotContain(afterRange.Id, ids);    // When after the range
        Assert.DoesNotContain(dateless.Id, ids);      // no When ⇒ never on the timeline

        // The timeline is single-point: each task sits on its one When date.
        var row = Assert.Single(timeline, item => item.Id == insideRange.Id);
        Assert.Equal(new DateOnly(2026, 6, 12), row.Date);
        Assert.Equal(Priority.P2, row.Priority);
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
        // references: a project/label that no record exists for.
        var missingGroup = Guid.NewGuid();
        var missingTag = Guid.NewGuid();
        var orphan = new TaskItem
        {
            Title = "떠 있는 참조",
            When = OnDay(Today),
            TaskGroupId = missingGroup,
            TagIds = { missingTag },
        };
        await store.SaveAsync(orphan);

        // Time views never resolve references, so the orphan surfaces normally.
        Assert.Contains(await store.GetTodayAsync(), t => t.Id == orphan.Id);
        // Classification queries by the dangling ids just return it — nothing joins or throws.
        Assert.Contains(await store.GetByTaskGroupAsync(missingGroup), t => t.Id == orphan.Id);
        Assert.Contains(await store.GetByTagAsync(missingTag), t => t.Id == orphan.Id);

        // A full rebuild from the files tolerates the same floating references.
        await store.InitializeAsync();
        Assert.Contains(await store.GetTodayAsync(), t => t.Id == orphan.Id);
    }

    [Fact]
    public async Task Checklist_IsMirroredInTheIndex_AndRebuildsFromFiles()
    {
        var root = NewRoot();
        var clock = new MutableTimeProvider(Now);
        await using var store = await OpenAsync(root, clock);
        var task = new TaskItem { Title = "부모" };
        task.Checklist.Add(new ChecklistItem { Title = "열린 항목" });
        task.Checklist.Add(new ChecklistItem { Title = "완료한 항목", IsChecked = true });
        await store.SaveAsync(task);

        var listed = Assert.Single(await store.GetAllActiveAsync());
        Assert.NotNull(listed.Checklist);
        Assert.Equal(new[] { "열린 항목", "완료한 항목" }, listed.Checklist!.Select(c => c.Title));
        Assert.False(listed.Checklist![0].IsChecked);
        Assert.True(listed.Checklist![1].IsChecked);

        // The checklist projection survives a full index rebuild from the files (it is derived, not stored).
        await store.InitializeAsync();
        var rebuilt = Assert.Single(await store.GetAllActiveAsync());
        Assert.Equal(2, rebuilt.Checklist!.Count);
        Assert.True(rebuilt.Checklist![1].IsChecked);
    }

    [Fact]
    public async Task DetailedTaskEdit_IsImmediatelyReflectedAcrossClassificationAndTimeViews()
    {
        var root = NewRoot();
        var clock = new MutableTimeProvider(Now);
        await using var store = await OpenAsync(root, clock);
        var project = new TaskGroup { Name = "옮길 프로젝트" };
        var label = new Tag { Name = "붙일 라벨" };
        var task = new TaskItem { Title = "편집 전" };
        await store.SaveAsync(project);
        await store.SaveAsync(label);
        await store.SaveAsync(task);
        Assert.Equal(task.Id, Assert.Single(await store.GetAllActiveAsync()).Id);

        task.Title = "편집 후";
        task.Priority = Priority.P1;
        task.TaskGroupId = project.Id;
        task.TagIds.Add(label.Id);
        task.When = OnDay(Today.AddDays(3));
        await store.SaveAsync(task);

        // Filing it under a group does not remove it from the home "모든 할 일" (All) view.
        Assert.Equal(task.Id, Assert.Single(await store.GetAllActiveAsync()).Id);
        Assert.Equal(task.Id, Assert.Single(await store.GetByTaskGroupAsync(project.Id)).Id);
        Assert.Equal(task.Id, Assert.Single(await store.GetByTagAsync(label.Id)).Id);
        var indexed = Assert.Single(await store.GetUpcomingAsync(), item => item.Id == task.Id);
        Assert.Equal("편집 후", indexed.Title);
        Assert.Equal(Priority.P1, indexed.Priority);
        Assert.Equal(Today.AddDays(3), indexed.WhenDate);
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public MutableTimeProvider(DateTimeOffset start) => Now = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
