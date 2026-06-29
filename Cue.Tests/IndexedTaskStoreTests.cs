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

        var project = new TaskGroup { Name = "프로젝트" };
        var todayTask = new TaskItem { Title = "오늘 할 일", When = OnDay(Today), TaskGroupId = project.Id };
        var doneTask = new TaskItem { Title = "끝낸 일", CompletedAt = Now.AddDays(-1) };

        await using (var store = await OpenAsync(root, clock))
        {
            await store.SaveAsync(project);
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

            var inGroup = await reopened.GetByTaskGroupAsync(project.Id);
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
        // Explicit sort orders keep the list order deterministic; the completed task is excluded outright
        // (these inbox lists are open-only — completed work doesn't need filing).
        var unfiled = new TaskItem { Title = "미분류", SortOrder = "b" };
        var grouped = new TaskItem { Title = "그룹에 든 일", TaskGroupId = project.Id, SortOrder = "c" };
        var tagged = new TaskItem { Title = "태그 붙은 일", TagIds = { label.Id }, SortOrder = "d" };
        var doneUnfiled = new TaskItem { Title = "끝낸 미분류", CompletedAt = Now, SortOrder = "a" };
        await store.SaveAsync(unfiled);
        await store.SaveAsync(grouped);
        await store.SaveAsync(tagged);
        await store.SaveAsync(doneUnfiled);

        // 그룹 없음: open tasks with no group (tagged-but-ungrouped included); the completed one is excluded.
        Assert.Equal(
            new[] { unfiled.Id, tagged.Id },
            (await store.GetWithoutTaskGroupAsync()).Select(t => t.Id));
        // 태그 없음: open tasks carrying no label (grouped-but-untagged included); completed excluded.
        Assert.Equal(
            new[] { unfiled.Id, grouped.Id },
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
        // The open one stays in the home "모든 할 일" view; the completed one is excluded (it lives in the
        // Logbook now, not dimmed in place).
        Assert.Equal(new[] { open.Id }, (await store.GetAllActiveAsync()).Select(t => t.Id));
        Assert.Contains(await store.GetLogbookAsync(), t => t.Id == done.Id);
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

        var project = new TaskGroup { Name = "프로젝트" };
        var label = new Tag { Name = "태그" };
        var task = new TaskItem
        {
            Title = "삭제 대상",
            When = OnDay(Today),
            TaskGroupId = project.Id,
            TagIds = { label.Id },
        };
        await store.SaveAsync(project);
        await store.SaveAsync(label);
        await store.SaveAsync(task);

        // Present everywhere it should be before deletion.
        Assert.Contains(await store.GetTodayAsync(), t => t.Id == task.Id);
        Assert.Contains(await store.GetByTaskGroupAsync(project.Id), t => t.Id == task.Id);
        Assert.Contains(await store.GetByTagAsync(label.Id), t => t.Id == task.Id);

        await store.DeleteAsync<TaskItem>(task.Id);

        // Gone from every active view (tombstones are excluded by default).
        Assert.DoesNotContain(await store.GetTodayAsync(), t => t.Id == task.Id);
        Assert.DoesNotContain(await store.GetByTaskGroupAsync(project.Id), t => t.Id == task.Id);
        Assert.DoesNotContain(await store.GetByTagAsync(label.Id), t => t.Id == task.Id);
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

        var first = new TaskGroup { Name = "첫 그룹" };
        var second = new TaskGroup { Name = "둘째 그룹" };
        var task = new TaskItem { Title = "이동하는 일", When = OnDay(Today), TaskGroupId = first.Id };

        // Save writes both: the file exists and the index query returns it.
        await store.SaveAsync(first);
        await store.SaveAsync(second);
        await store.SaveAsync(task);
        Assert.True(File.Exists(Path.Combine(root, "tasks", task.Id + ".json")));
        Assert.Contains(await store.GetByTaskGroupAsync(first.Id), t => t.Id == task.Id);

        // Re-saving an edit reflects into the index immediately — no stale row, no rebuild needed.
        task.TaskGroupId = second.Id;
        await store.SaveAsync(task);
        Assert.DoesNotContain(await store.GetByTaskGroupAsync(first.Id), t => t.Id == task.Id);
        Assert.Contains(await store.GetByTaskGroupAsync(second.Id), t => t.Id == task.Id);

        // Delete updates both: file becomes a tombstone, index drops it from active views.
        await store.DeleteAsync<TaskItem>(task.Id);
        Assert.True(File.Exists(Path.Combine(root, "tasks", task.Id + ".json")));
        Assert.DoesNotContain(await store.GetByTaskGroupAsync(second.Id), t => t.Id == task.Id);
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
        var filed = new TaskGroup { Name = "분류된 그룹" };
        var inbox = new TaskItem { Title = "미분류", When = ScheduledWhen.Unscheduled };                            // Inbox + Anytime
        var anytimeFiled = new TaskItem { Title = "언젠가", When = ScheduledWhen.Unscheduled, TaskGroupId = filed.Id };  // Anytime (filed)
        var todayTask = new TaskItem { Title = "오늘 할 일", When = OnDay(Today), TaskGroupId = filed.Id };              // Today
        var future = new TaskItem { Title = "다가오는 일", When = OnDay(Today.AddDays(2)), TaskGroupId = filed.Id };      // Upcoming
        var done = new TaskItem { Title = "완료", When = OnDay(Today), CompletedAt = Now, TaskGroupId = filed.Id };      // Logbook only

        await store.SaveAsync(filed);
        foreach (var t in new[] { inbox, anytimeFiled, todayTask, future, done })
            await store.SaveAsync(t);

        // The home "모든 할 일" (All) spans every group: it returns every non-deleted OPEN task, grouped or
        // not — the completed one is excluded.
        Assert.Equal(
            new[] { inbox.Id, anytimeFiled.Id, todayTask.Id, future.Id }.OrderBy(g => g),
            (await store.GetAllActiveAsync()).Select(t => t.Id).OrderBy(g => g));

        // Anytime ("언젠가") = non-deleted open tasks with no When date, regardless of project.
        Assert.Equal(
            new[] { inbox.Id, anytimeFiled.Id }.OrderBy(g => g),
            (await store.GetAnytimeAsync()).Select(t => t.Id).OrderBy(g => g));

        var today = (await store.GetTodayAsync()).Select(t => t.Id).ToHashSet();
        Assert.Contains(todayTask.Id, today);
        Assert.DoesNotContain(done.Id, today);          // completed is excluded from the open Today list
        Assert.DoesNotContain(future.Id, today);        // future When

        // The completed task (finished today) surfaces in the Today view's "오늘 완료한 일" section instead.
        Assert.Equal(new[] { done.Id }, (await store.GetTodayCompletedAsync()).Select(t => t.Id));

        var upcoming = (await store.GetUpcomingAsync()).Select(t => t.Id).ToHashSet();
        Assert.Contains(future.Id, upcoming);        // future When

        Assert.Equal(new[] { done.Id }, (await store.GetLogbookAsync()).Select(t => t.Id));
    }

    [Fact]
    public async Task GetByPriority_ReturnsOpenTasksOrderedByPriority_UnprioritizedLast_CompletedExcluded()
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

        // Results run P1 → P4 then unprioritized tasks last. Completed tasks are excluded entirely — the
        // 중요도 view is a lens on open ranked work — so the completed P1 does not appear.
        Assert.Equal(new[] { p1.Id, p2.Id, none.Id }, ids);
    }

    [Fact]
    public async Task CompletedSections_ExposeCompletedWorkPerToday_Group_AndTag()
    {
        var root = NewRoot();
        var clock = new MutableTimeProvider(Now);
        await using var store = await OpenAsync(root, clock);

        var project = new TaskGroup { Name = "프로젝트" };
        var label = new Tag { Name = "라벨" };
        await store.SaveAsync(project);
        await store.SaveAsync(label);

        // A task completed today, inside the group and carrying the tag.
        var doneToday = new TaskItem { Title = "오늘 끝낸 일", TaskGroupId = project.Id, TagIds = { label.Id }, CompletedAt = Now };
        // A task completed yesterday, inside the group — group/tag completed lists keep it, but it is not
        // "completed today" so it stays out of the Today section.
        var doneYesterday = new TaskItem { Title = "어제 끝낸 일", TaskGroupId = project.Id, CompletedAt = Now.AddDays(-1) };
        // An open task in the group: never in any completed list.
        var open = new TaskItem { Title = "열린 일", TaskGroupId = project.Id };
        foreach (var t in new[] { doneToday, doneYesterday, open })
            await store.SaveAsync(t);

        // 오늘 완료한 일: only what was completed within the current local day.
        Assert.Equal(new[] { doneToday.Id }, (await store.GetTodayCompletedAsync()).Select(t => t.Id));

        // The group / tag completed sections gather that container's completed work, newest first, and
        // exclude the open task.
        Assert.Equal(new[] { doneToday.Id, doneYesterday.Id }, (await store.GetCompletedByTaskGroupAsync(project.Id)).Select(t => t.Id));
        Assert.Equal(new[] { doneToday.Id }, (await store.GetCompletedByTagAsync(label.Id)).Select(t => t.Id));

        // The open lists never carry the completed rows, and each completed row carries its completion
        // instant so the Logbook can group by day.
        Assert.Equal(new[] { open.Id }, (await store.GetByTaskGroupAsync(project.Id)).Select(t => t.Id));
        Assert.All(await store.GetLogbookAsync(), row => Assert.NotNull(row.CompletedAt));
    }

    [Fact]
    public async Task KeepCompletedToday_KeepsTodaysCompletionsInPlaceOnActiveLists_ButNotOlderOnes()
    {
        var root = NewRoot();
        var clock = new MutableTimeProvider(Now);
        await using var store = await OpenAsync(root, clock);

        var project = new TaskGroup { Name = "프로젝트" };
        var label = new Tag { Name = "라벨" };
        await store.SaveAsync(project);
        await store.SaveAsync(label);

        var open = new TaskItem { Title = "열린 일", When = OnDay(Today), TaskGroupId = project.Id, TagIds = { label.Id } };
        var doneToday = new TaskItem { Title = "오늘 끝낸 일", When = OnDay(Today), TaskGroupId = project.Id, TagIds = { label.Id }, CompletedAt = Now };
        var doneYesterday = new TaskItem { Title = "어제 끝낸 일", When = OnDay(Today), TaskGroupId = project.Id, TagIds = { label.Id }, CompletedAt = Now.AddDays(-1) };
        foreach (var t in new[] { open, doneToday, doneYesterday })
            await store.SaveAsync(t);

        // Open-only (the default) hides every completion from the active lists.
        Assert.Equal(new[] { open.Id }, (await store.GetTodayAsync()).Select(t => t.Id));
        Assert.Equal(new[] { open.Id }, (await store.GetByTaskGroupAsync(project.Id)).Select(t => t.Id));
        Assert.Equal(new[] { open.Id }, (await store.GetByTagAsync(label.Id)).Select(t => t.Id));
        Assert.Equal(new[] { open.Id }, (await store.GetAllActiveAsync()).Select(t => t.Id));

        // keepCompletedToday on: today's completion stays in place (open work plus the just-finished one),
        // but yesterday's completion does not — it has already rolled into the completed section / Logbook.
        Assert.Equal(
            new[] { open.Id, doneToday.Id }.OrderBy(g => g.ToString()),
            (await store.GetTodayAsync(keepCompletedToday: true)).Select(t => t.Id).OrderBy(g => g.ToString()));
        Assert.Equal(
            new[] { open.Id, doneToday.Id }.OrderBy(g => g.ToString()),
            (await store.GetByTaskGroupAsync(project.Id, keepCompletedToday: true)).Select(t => t.Id).OrderBy(g => g.ToString()));
        Assert.Equal(
            new[] { open.Id, doneToday.Id }.OrderBy(g => g.ToString()),
            (await store.GetByTagAsync(label.Id, keepCompletedToday: true)).Select(t => t.Id).OrderBy(g => g.ToString()));
        Assert.Equal(
            new[] { open.Id, doneToday.Id }.OrderBy(g => g.ToString()),
            (await store.GetAllActiveAsync(keepCompletedToday: true)).Select(t => t.Id).OrderBy(g => g.ToString()));

        // The paired completed sections drop today's row when excludeKeptInPlace is set, so a kept-in-place
        // task is never listed twice; yesterday's completion remains and the counts agree.
        Assert.Equal(new[] { doneYesterday.Id }, (await store.GetCompletedByTaskGroupAsync(project.Id, excludeKeptInPlace: true)).Select(t => t.Id));
        Assert.Equal(1, await store.GetCompletedCountByTaskGroupAsync(project.Id, excludeKeptInPlace: true));
        Assert.Equal(new[] { doneYesterday.Id }, (await store.GetCompletedByTagAsync(label.Id, excludeKeptInPlace: true)).Select(t => t.Id));
        Assert.Equal(1, await store.GetCompletedCountByTagAsync(label.Id, excludeKeptInPlace: true));

        // Without the exclude flag the sections still show every completion (the open-only behavior).
        Assert.Equal(new[] { doneToday.Id, doneYesterday.Id }, (await store.GetCompletedByTaskGroupAsync(project.Id)).Select(t => t.Id));
        Assert.Equal(2, await store.GetCompletedCountByTaskGroupAsync(project.Id));

        // At the day rollover, yesterday's-from-the-new-day completion (today's task) leaves the active list:
        // advancing the clock a day means it is no longer "completed today", so even with keep on it drops out.
        clock.Now = Now.AddDays(1);
        Assert.DoesNotContain(await store.GetTodayAsync(keepCompletedToday: true), t => t.Id == doneToday.Id);
        Assert.DoesNotContain(await store.GetByTaskGroupAsync(project.Id, keepCompletedToday: true), t => t.Id == doneToday.Id);
        // It now belongs to the completed section even when today's rows are excluded (it is no longer today's).
        Assert.Contains(await store.GetCompletedByTaskGroupAsync(project.Id, excludeKeptInPlace: true), t => t.Id == doneToday.Id);
    }

    [Fact]
    public async Task KeepCompletedToday_OnTheTodayView_AdmitsTodaysCompletionsRegardlessOfWhen()
    {
        var root = NewRoot();
        var clock = new MutableTimeProvider(Now);
        await using var store = await OpenAsync(root, clock);

        // A future-dated and an unscheduled task, both completed today: open they'd never be in Today, but the
        // "오늘 완료한 일" section shows them — so kept-in-place, the Today view must admit them too (matching
        // that section's set), regardless of their When.
        var futureDone = new TaskItem { Title = "미리 끝낸 미래 일", When = OnDay(Today.AddDays(3)), CompletedAt = Now };
        var unscheduledDone = new TaskItem { Title = "끝낸 언젠가 일", CompletedAt = Now };
        var dueOpen = new TaskItem { Title = "오늘 열린 일", When = OnDay(Today) };
        foreach (var t in new[] { futureDone, unscheduledDone, dueOpen })
            await store.SaveAsync(t);

        var todayKept = (await store.GetTodayAsync(keepCompletedToday: true)).Select(t => t.Id).ToHashSet();
        Assert.Contains(dueOpen.Id, todayKept);
        Assert.Contains(futureDone.Id, todayKept);
        Assert.Contains(unscheduledDone.Id, todayKept);

        // Open-only, none of the completed ones appear and only the due-today open task is there.
        Assert.Equal(new[] { dueOpen.Id }, (await store.GetTodayAsync()).Select(t => t.Id));
    }

    [Fact]
    public async Task KeepCompletedToday_NeverKeepsAnEndedRecurringSeries_ItGoesToTheCompletedSection()
    {
        var root = NewRoot();
        var clock = new MutableTimeProvider(Now);
        await using var store = await OpenAsync(root, clock);

        var project = new TaskGroup { Name = "프로젝트" };
        await store.SaveAsync(project);

        // A non-recurring task completed today (kept in place) vs. an ended recurring series completed today
        // (a recurring record — CompletedAt set, rule kept — which must NOT linger as a dimmed active row).
        var oneOff = new TaskItem { Title = "단발 완료", When = OnDay(Today), TaskGroupId = project.Id, CompletedAt = Now };
        var endedSeries = new TaskItem
        {
            Title = "반복 종료",
            When = OnDay(Today),
            TaskGroupId = project.Id,
            CompletedAt = Now,
            Recurrence = new RecurrenceRule("FREQ=DAILY", OnDayZoned(Today)),
        };
        await store.SaveAsync(oneOff);
        await store.SaveAsync(endedSeries);

        // Kept-in-place admits the one-off but never the ended series.
        Assert.Equal(new[] { oneOff.Id }, (await store.GetTodayAsync(keepCompletedToday: true)).Select(t => t.Id));
        Assert.Equal(new[] { oneOff.Id }, (await store.GetByTaskGroupAsync(project.Id, keepCompletedToday: true)).Select(t => t.Id));

        // The completed section, excluding the kept-in-place set, then surfaces the ended series (and not the
        // one-off, which is being shown in place) — so 반복 종료 still appears, just not as an active row.
        Assert.Equal(new[] { endedSeries.Id }, (await store.GetTodayCompletedAsync(excludeKeptInPlace: true)).Select(t => t.Id));
        Assert.Equal(1, await store.GetTodayCompletedCountAsync(excludeKeptInPlace: true));
        Assert.Equal(new[] { endedSeries.Id }, (await store.GetCompletedByTaskGroupAsync(project.Id, excludeKeptInPlace: true)).Select(t => t.Id));
        Assert.Equal(1, await store.GetCompletedCountByTaskGroupAsync(project.Id, excludeKeptInPlace: true));

        // Both remain in the Logbook regardless.
        var logbook = (await store.GetLogbookAsync()).Select(t => t.Id).ToHashSet();
        Assert.Contains(oneOff.Id, logbook);
        Assert.Contains(endedSeries.Id, logbook);
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
    public async Task Save_NormalizesMissingDeletedAndDuplicateReferences()
    {
        var root = NewRoot();
        var clock = new MutableTimeProvider(Now);
        await using var store = await OpenAsync(root, clock);

        var deletedGroup = new TaskGroup { Name = "삭제된 그룹" };
        var keptTag = new Tag { Name = "유지할 태그" };
        var deletedTag = new Tag { Name = "삭제된 태그" };
        await store.SaveAsync(deletedGroup);
        await store.SaveAsync(keptTag);
        await store.SaveAsync(deletedTag);
        await store.DeleteAsync<TaskGroup>(deletedGroup.Id);
        await store.DeleteAsync<Tag>(deletedTag.Id);

        var missingGroup = Guid.NewGuid();
        var missingTag = Guid.NewGuid();
        var task = new TaskItem
        {
            Title = "참조 정규화",
            When = OnDay(Today),
            TaskGroupId = deletedGroup.Id,
            TagIds = { keptTag.Id, missingTag, deletedTag.Id, keptTag.Id },
        };
        await store.SaveAsync(task);

        var persisted = (await store.GetAsync<TaskItem>(task.Id))!;
        Assert.Null(persisted.TaskGroupId);
        Assert.Equal(new[] { keptTag.Id }, persisted.TagIds);

        task.TaskGroupId = missingGroup;
        await store.SaveAsync(task);
        persisted = (await store.GetAsync<TaskItem>(task.Id))!;
        Assert.Null(persisted.TaskGroupId);
        Assert.DoesNotContain(await store.GetByTaskGroupAsync(missingGroup), item => item.Id == task.Id);
        Assert.DoesNotContain(await store.GetByTagAsync(missingTag), item => item.Id == task.Id);
        Assert.Equal(task.Id, Assert.Single(await store.GetByTagAsync(keptTag.Id)).Id);

        // The normalized file is the source of truth, so a rebuild preserves the repaired state.
        await store.InitializeAsync();
        Assert.Equal(task.Id, Assert.Single(await store.GetWithoutTaskGroupAsync()).Id);
        Assert.Equal(task.Id, Assert.Single(await store.GetByTagAsync(keptTag.Id)).Id);
    }

    [Fact]
    public async Task Save_PreservesReferencesWhenContainersAreUnreadable()
    {
        var root = NewRoot();
        var clock = new MutableTimeProvider(Now);
        await using var store = await OpenAsync(root, clock);

        var group = new TaskGroup { Name = "잠긴 그룹" };
        var tag = new Tag { Name = "잠긴 태그" };
        await store.SaveAsync(group);
        await store.SaveAsync(tag);

        var groupPath = Path.Combine(root, "groups", group.Id + ".json");
        var tagPath = Path.Combine(root, "tags", tag.Id + ".json");
        await using var lockedGroup = new FileStream(groupPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await using var lockedTag = new FileStream(tagPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var task = new TaskItem
        {
            Title = "읽을 수 없는 참조 보존",
            TaskGroupId = group.Id,
            TagIds = { tag.Id, tag.Id },
        };
        await store.SaveAsync(task);

        var persisted = (await store.GetAsync<TaskItem>(task.Id))!;
        Assert.Equal(group.Id, persisted.TaskGroupId);
        Assert.Equal(new[] { tag.Id }, persisted.TagIds);
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
    public async Task IsRecurring_IsMirroredInTheIndex_AndRebuildsFromFiles()
    {
        var root = NewRoot();
        var clock = new MutableTimeProvider(Now);
        await using var store = await OpenAsync(root, clock);

        var repeating = new TaskItem
        {
            Title = "매주 운동",
            Recurrence = new RecurrenceRule("FREQ=WEEKLY;BYDAY=MO", OnDayZoned(Today)),
        };
        var oneOff = new TaskItem { Title = "한 번만" };
        await store.SaveAsync(repeating);
        await store.SaveAsync(oneOff);

        var listed = await store.GetAllActiveAsync();
        Assert.True(Assert.Single(listed, t => t.Id == repeating.Id).IsRecurring);
        Assert.False(Assert.Single(listed, t => t.Id == oneOff.Id).IsRecurring);

        // Clearing the recurrence flips the flag on the next reflect.
        repeating.Recurrence = null;
        await store.SaveAsync(repeating);
        Assert.False(Assert.Single(await store.GetAllActiveAsync(), t => t.Id == repeating.Id).IsRecurring);

        // The flag is derived, so it survives a full index rebuild from the files.
        oneOff.Recurrence = new RecurrenceRule("FREQ=DAILY", OnDayZoned(Today));
        await store.SaveAsync(oneOff);
        await store.InitializeAsync();
        Assert.True(Assert.Single(await store.GetAllActiveAsync(), t => t.Id == oneOff.Id).IsRecurring);
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
