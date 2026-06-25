using Cue.Domain;
using Cue.Parsing;
using Cue.Storage;
using Cue.Storage.Ranking;
using Cue.Storage.Recurrence;
using Cue.ViewModels;

namespace Cue.Tests;

public sealed class ViewModelRegressionTests
{
    [Fact]
    public async Task DetailSavePreservesWhenTime()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var task = new TaskItem
        {
            Title = "meeting",
            When = ScheduledWhen.On(ZonedDateTime.FromLocal(new DateTime(2026, 6, 24, 15, 30, 0), "UTC")),
        };
        await store.SaveAsync(task);

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        await vm.Detail.OpenAsync(task.Id);
        await vm.Detail.FlushAsync();

        var saved = await store.GetAsync<TaskItem>(task.Id);
        Assert.NotNull(saved);
        Assert.Equal(new TimeSpan(15, 30, 0), saved.When.Date!.Value.ToLocal().TimeOfDay);
    }

    [Fact]
    public async Task DetailSavePreservesTodayTime()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var task = new TaskItem
        {
            Title = "today at 20:10",
            When = ScheduledWhen.On(ZonedDateTime.FromLocal(new DateTime(2026, 6, 23, 20, 10, 0), "UTC")),
        };
        await store.SaveAsync(task);

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        await vm.Detail.OpenAsync(task.Id);
        await vm.Detail.FlushAsync();

        var saved = await store.GetAsync<TaskItem>(task.Id);
        Assert.NotNull(saved);
        Assert.Equal(WhenKind.OnDate, saved!.When.Kind);
        Assert.Equal(new TimeSpan(20, 10, 0), saved.When.Date!.Value.ToLocal().TimeOfDay);
    }

    [Fact]
    public async Task DetailSaveWithoutDateEditsPreservesOriginalTimeZoneAndInstant()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var originalWhen = ZonedDateTime.FromLocal(new DateTime(2026, 6, 24, 15, 30, 0), "Korea Standard Time");
        var task = new TaskItem { Title = "zoned", When = ScheduledWhen.On(originalWhen) };
        await store.SaveAsync(task);

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        await vm.Detail.OpenAsync(task.Id);
        vm.Detail.Title = "title only";
        await vm.Detail.FlushAsync();

        var saved = await store.GetAsync<TaskItem>(task.Id);
        Assert.NotNull(saved);
        Assert.Equal(originalWhen, saved.When.Date);
    }

    [Fact]
    public async Task CompletingHoldsTheRowUntilFinalize_ThenItLeavesTheOpenList()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var task = new TaskItem { Title = "complete me" };
        await store.SaveAsync(task);

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        await vm.LoadCommand.ExecuteAsync(null);
        var row = Assert.Single(vm.Tasks);

        // Completing saves immediately but does NOT reload the row away — it holds its place for the
        // acknowledgement moment (the View runs the timing and later calls FinalizeCompletionAsync).
        row.SetCompletedSilently(true);
        await vm.ToggleCompleteCommand.ExecuteAsync(row);
        Assert.Same(row, Assert.Single(vm.Tasks));
        Assert.True((await store.GetAsync<TaskItem>(task.Id))!.IsCompleted);

        // Finalizing (the View's fold has played) reloads and drops the now-completed task out of the open
        // 모든 할 일 list entirely — completed work is excluded there.
        await vm.FinalizeCompletionAsync(row);
        Assert.Empty(vm.Tasks);
    }

    [Fact]
    public async Task TodayCompletion_MovesTaskIntoTheCollapsedCompletedSection()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var task = new TaskItem { Title = "오늘 할 일", When = ScheduledWhen.AllDay(ZonedDateTime.FromLocal(new DateTime(2026, 6, 23, 0, 0, 0), "UTC")) };
        await store.SaveAsync(task);

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        vm.SetNavigation(new TaskListNavigation(TaskListMode.Today));
        await vm.LoadCommand.ExecuteAsync(null);
        var row = Assert.Single(vm.Tasks);
        Assert.False(vm.CompletedSection.HasItems);

        row.SetCompletedSilently(true);
        await vm.ToggleCompleteCommand.ExecuteAsync(row);
        await vm.FinalizeCompletionAsync(row);

        // Out of the open Today list, into the collapsed "오늘 완료한 일" section. While collapsed the section
        // shows only its count (1) — its rows are lazy, so no completed TaskRowViewModel is realized yet.
        Assert.Empty(vm.Tasks);
        Assert.True(vm.CompletedSection.HasItems);
        Assert.False(vm.CompletedSection.IsExpanded);   // starts collapsed
        Assert.Equal("오늘 완료한 일", vm.CompletedSection.Title);
        Assert.Equal(1, vm.CompletedSection.TotalCount);
        Assert.Empty(vm.CompletedSection.Tasks);

        // Expanding pages the rows in: now the completed row is realized.
        await vm.CompletedSection.ToggleExpandedCommand.ExecuteAsync(null);
        Assert.True(vm.CompletedSection.IsExpanded);
        Assert.Equal(task.Id, Assert.Single(vm.CompletedSection.Tasks).Id);
    }

    [Fact]
    public async Task CompletedSection_LazyLoadsRowsInPages_AndKeepsThemAcrossRefresh()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);

        var group = new TaskGroup { Name = "프로젝트" };
        await store.SaveAsync(group);

        // 250 completed tasks in the group — comfortably more than the 100-row page size.
        for (var i = 0; i < 250; i++)
            await store.SaveAsync(new TaskItem
            {
                Title = $"끝낸 일 {i:000}",
                TaskGroupId = group.Id,
                CompletedAt = new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero).AddSeconds(i),
            });

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        vm.SetNavigation(new TaskListNavigation(TaskListMode.TaskGroup, group.Id));
        await vm.LoadCommand.ExecuteAsync(null);

        // Collapsed: the count is known from the COUNT query, but not a single row is realized.
        Assert.Equal(250, vm.CompletedSection.TotalCount);
        Assert.Empty(vm.CompletedSection.Tasks);
        Assert.True(vm.CompletedSection.HasItems);
        Assert.True(vm.CompletedSection.HasMore);

        // First expand pages in one batch (100).
        await vm.CompletedSection.ToggleExpandedCommand.ExecuteAsync(null);
        Assert.Equal(100, vm.CompletedSection.Tasks.Count);
        Assert.True(vm.CompletedSection.CanLoadMore);

        // "더 보기" pulls each further page: 200, then the final 250 (clamped to the total).
        await vm.CompletedSection.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(200, vm.CompletedSection.Tasks.Count);
        await vm.CompletedSection.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(250, vm.CompletedSection.Tasks.Count);
        Assert.False(vm.CompletedSection.HasMore);
        Assert.False(vm.CompletedSection.CanLoadMore);

        // A refresh re-fetches the same window, so the opened section keeps all its realized rows rather
        // than collapsing back to the count.
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(250, vm.CompletedSection.Tasks.Count);
    }

    [Fact]
    public async Task LogbookGroupsCompletedTasksByDay()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 24, 5, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        await store.SaveAsync(new TaskItem { Title = "오늘 끝", CompletedAt = new DateTimeOffset(2026, 6, 24, 3, 0, 0, TimeSpan.Zero) });
        await store.SaveAsync(new TaskItem { Title = "어제 끝", CompletedAt = new DateTimeOffset(2026, 6, 23, 3, 0, 0, TimeSpan.Zero) });
        await store.SaveAsync(new TaskItem { Title = "그제 끝", CompletedAt = new DateTimeOffset(2026, 6, 22, 3, 0, 0, TimeSpan.Zero) });

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        vm.SetNavigation(new TaskListNavigation(TaskListMode.Logbook));
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.True(vm.IsLogbookSectioned);
        // Newest day first, with friendly headings for the two most recent days and a date for older ones.
        Assert.Equal(new[] { "오늘", "어제", "6월 22일" }, vm.LogbookSections.Select(s => s.DisplayTitle));
        Assert.All(vm.LogbookSections, s => Assert.Single(s.Tasks));
    }

    [Fact]
    public async Task LogbookKeepsSameDayInDifferentYearsAsSeparateSections()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 24, 5, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        // Two tasks completed on the same month/day a year apart: older days render without the year, so
        // both read "6월 22일" — they must stay distinct sections, not collapse into one.
        await store.SaveAsync(new TaskItem { Title = "올해 끝", CompletedAt = new DateTimeOffset(2026, 6, 22, 3, 0, 0, TimeSpan.Zero) });
        await store.SaveAsync(new TaskItem { Title = "작년 끝", CompletedAt = new DateTimeOffset(2025, 6, 22, 3, 0, 0, TimeSpan.Zero) });

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        vm.SetNavigation(new TaskListNavigation(TaskListMode.Logbook));
        await vm.LoadCommand.ExecuteAsync(null);

        // Two sections, newest first: this year's drops the year, last year's carries it.
        Assert.Equal(new[] { "6월 22일", "2025년 6월 22일" }, vm.LogbookSections.Select(s => s.DisplayTitle));
        Assert.All(vm.LogbookSections, s => Assert.Single(s.Tasks));
    }

    [Fact]
    public async Task Checklist_Add_Toggle_Delete_PersistThroughTheStore()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var task = new TaskItem { Title = "parent" };
        await store.SaveAsync(task);

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        await vm.LoadCommand.ExecuteAsync(null);
        await vm.Detail.OpenAsync(task.Id);

        // Add a checklist item from the detail panel.
        vm.Detail.NewChecklistItemTitle = "사기";
        await vm.Detail.AddChecklistItemCommand.ExecuteAsync(null);
        var item = Assert.Single((await store.GetAsync<TaskItem>(task.Id))!.Checklist);
        Assert.Equal("사기", item.Title);
        Assert.False(item.IsChecked);

        // It shows as a nested row in the list; ticking it from there persists onto the parent.
        var checkRow = Assert.Single(Assert.Single(vm.Tasks).ChecklistItems);
        Assert.Equal(item.Id, checkRow.Id);
        checkRow.SetCheckedSilently(true);
        await vm.ToggleChecklistItemCommand.ExecuteAsync(checkRow);
        Assert.True((await store.GetAsync<TaskItem>(task.Id))!.Checklist[0].IsChecked);

        // Deleting it from the detail panel removes it outright (no tombstone for an embedded item).
        await vm.Detail.DeleteChecklistItemCommand.ExecuteAsync(item.Id);
        Assert.Empty((await store.GetAsync<TaskItem>(task.Id))!.Checklist);
        Assert.Empty(vm.Detail.Checklist);
    }

    [Fact]
    public async Task Checklist_InlineToggle_ReusesRowInstance_AndKeepsCheckedDimAcrossReload()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var task = new TaskItem { Title = "parent" };
        task.Checklist.Add(new ChecklistItem { Title = "항목" });
        await store.SaveAsync(task);

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        await vm.LoadCommand.ExecuteAsync(null);
        var row = Assert.Single(Assert.Single(vm.Tasks).ChecklistItems);

        // A bare reload (every save routes through one) must reconcile in place, not rebuild: the same
        // row instance survives so the checkbox the user is interacting with is never torn down.
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Same(row, Assert.Single(Assert.Single(vm.Tasks).ChecklistItems));

        // Ticking it persists and — after the toggle's own reload — the reused row reflects checked + dim.
        row.SetCheckedSilently(true);
        await vm.ToggleChecklistItemCommand.ExecuteAsync(row);
        var afterToggle = Assert.Single(Assert.Single(vm.Tasks).ChecklistItems);
        Assert.Same(row, afterToggle);
        Assert.True(afterToggle.IsChecked);
        Assert.Equal(0.48, afterToggle.VisualOpacity);

        // Unchecking from the same instance works too (the bug was that this never took).
        row.SetCheckedSilently(false);
        await vm.ToggleChecklistItemCommand.ExecuteAsync(row);
        Assert.False((await store.GetAsync<TaskItem>(task.Id))!.Checklist[0].IsChecked);
        Assert.False(Assert.Single(Assert.Single(vm.Tasks).ChecklistItems).IsChecked);
    }

    [Fact]
    public async Task MutateAsync_SerializesReadModifyWrite_SoConcurrentFieldUpdatesDoNotClobber()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var task = new TaskItem { Title = "parent", Priority = Priority.None };
        task.Checklist.Add(new ChecklistItem { Title = "X" });
        await store.SaveAsync(task);

        // Two read-modify-writes of the same task, each touching a different field, launched without a
        // barrier between them. Because MutateAsync holds the store's write lock across the whole
        // read-modify-write, each one reads the other's committed result and writes back only its own
        // field — so neither resurrects a stale copy that drops the other's change.
        var metadata = store.MutateAsync<TaskItem>(task.Id, t => { t.Priority = Priority.P1; return true; });
        var checklist = store.MutateAsync<TaskItem>(task.Id, t => { t.Checklist[0].IsChecked = true; return true; });
        await Task.WhenAll(metadata, checklist);

        var saved = await store.GetAsync<TaskItem>(task.Id);
        Assert.Equal(Priority.P1, saved!.Priority);        // the metadata update survived
        Assert.True(saved.Checklist[0].IsChecked);          // the checklist update survived
    }

    [Fact]
    public async Task MutateAsync_ReturningFalse_SkipsTheSave_AndLeavesUpdatedAtUntouched()
    {
        using var temp = new TempDirectory();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var task = new TaskItem { Title = "parent" };
        await store.SaveAsync(task);
        var before = (await store.GetAsync<TaskItem>(task.Id))!.UpdatedAt;

        clock.Now = new DateTimeOffset(2026, 6, 23, 5, 0, 0, TimeSpan.Zero);
        var result = await store.MutateAsync<TaskItem>(task.Id, _ => false);

        Assert.Null(result); // declined to save
        Assert.Equal(before, (await store.GetAsync<TaskItem>(task.Id))!.UpdatedAt);
    }

    [Fact]
    public async Task DetailChecklistEditAndMetadataAutoSave_Interleaved_NeitherClobbersTheOther()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var task = new TaskItem { Title = "parent", Priority = Priority.None };
        task.Checklist.Add(new ChecklistItem { Title = "X" });
        await store.SaveAsync(task);

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        await vm.LoadCommand.ExecuteAsync(null);
        await vm.Detail.OpenAsync(task.Id);

        // Tick the checklist item (queues a checklist save) and change the priority (queues a metadata
        // save) back to back, without awaiting either — the exact cross the two independent save paths
        // could take. Draining both must leave each edit intact: the metadata save must not write back
        // an old checklist, and the checklist save must not write back an old priority.
        vm.Detail.Checklist[0].IsChecked = true;
        vm.Detail.SelectedPriority = Priority.P1;
        await vm.Detail.DrainPendingSaveAsync();

        var saved = await store.GetAsync<TaskItem>(task.Id);
        Assert.Equal(Priority.P1, saved!.Priority);
        Assert.True(Assert.Single(saved.Checklist).IsChecked);
    }

    [Fact]
    public async Task FlushAsync_WaitsForPendingChecklistSave_NotJustMetadata()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var task = new TaskItem { Title = "parent" };
        task.Checklist.Add(new ChecklistItem { Title = "X" });
        await store.SaveAsync(task);

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        await vm.Detail.OpenAsync(task.Id);

        // A checklist tick is fired and forgotten; the close/switch flush must still wait for it. Before
        // the fix FlushAsync only drained the metadata chain, so a tick made right before a switch could
        // be stranded and lost.
        vm.Detail.Checklist[0].IsChecked = true;
        await vm.Detail.FlushAsync();

        Assert.True(Assert.Single((await store.GetAsync<TaskItem>(task.Id))!.Checklist).IsChecked);
    }

    [Fact]
    public async Task ListCompletion_AndDetailMetadataEdit_Concurrent_NeitherClobbersTheOther()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var task = new TaskItem { Title = "task", Priority = Priority.None };
        await store.SaveAsync(task);

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        await vm.LoadCommand.ExecuteAsync(null);
        var row = Assert.Single(vm.Tasks);
        await vm.Detail.OpenAsync(task.Id);

        // Change the priority from the detail panel (queues a metadata autosave, which writes Priority
        // but never CompletedAt) and complete the same task from the list (which writes CompletedAt but
        // never Priority). Run through the completion service and drain the autosave. Because both go
        // through the store's atomic read-modify-write, the completion can't write back a stale priority
        // and the metadata save can't write back a stale (uncompleted) state.
        vm.Detail.SelectedPriority = Priority.P1;
        row.SetCompletedSilently(true);
        await vm.ToggleCompleteCommand.ExecuteAsync(row);
        await vm.Detail.DrainPendingSaveAsync();

        var saved = await store.GetAsync<TaskItem>(task.Id);
        Assert.Equal(Priority.P1, saved!.Priority);   // the detail edit survived the completion
        Assert.True(saved.IsCompleted);               // the completion survived the detail edit
    }

    [Fact]
    public async Task SegmentedTimeEditorSavesExactChosenTime()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var task = new TaskItem
        {
            Title = "precise time",
            When = ScheduledWhen.On(ZonedDateTime.FromLocal(new DateTime(2026, 6, 24, 9, 0, 0), "UTC")),
        };
        await store.SaveAsync(task);

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        await vm.Detail.OpenAsync(task.Id);
        vm.Detail.SelectedWhenHour = vm.Detail.Hours[13];
        vm.Detail.SelectedWhenMinute = vm.Detail.Minutes[5];
        await vm.Detail.DrainPendingSaveAsync();

        var saved = await store.GetAsync<TaskItem>(task.Id);
        Assert.Equal(new TimeSpan(13, 5, 0), saved!.When.Date!.Value.ToLocal().TimeOfDay);
    }

    [Fact]
    public async Task UnscheduledTaskOffersOptionalWhenEditor()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var task = new TaskItem { Title = "no date yet" };
        await store.SaveAsync(task);

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        await vm.Detail.OpenAsync(task.Id);

        // No date yet: the "+ 날짜 추가" affordance shows, the editor is hidden.
        Assert.True(vm.Detail.CanAddWhen);
        Assert.False(vm.Detail.IsWhenEditorVisible);

        vm.Detail.EnableWhenEditor();
        Assert.False(vm.Detail.CanAddWhen);
        Assert.True(vm.Detail.IsWhenEditorVisible);

        vm.Detail.ClearWhen();
        Assert.True(vm.Detail.CanAddWhen);
        Assert.False(vm.Detail.IsWhenEditorVisible);

        // Enabling/clearing the When editor autosaves; let it finish before the temp folder is torn down.
        await vm.Detail.DrainPendingSaveAsync();
    }

    [Fact]
    public async Task QuickAddWithTypedDate_BecomesOnDateWhen_DatelessStaysUnscheduled()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());

        // A typed date resolves to a single When (OnDate) — the task surfaces by that date.
        vm.QuickAddText = "다음주 금요일 회의";
        await vm.AddCommand.ExecuteAsync(null);

        var withDate = Assert.Single(await store.GetAllActiveAsync(), t => t.Title == "회의");
        Assert.Equal(WhenKind.OnDate, withDate.WhenKind);
        Assert.NotNull(withDate.WhenDate);

        // A genuinely dateless task off the Today list stays Unscheduled → lands in "언젠가" (Anytime).
        vm.QuickAddText = "장보기";
        await vm.AddCommand.ExecuteAsync(null);

        var dateless = Assert.Single(await store.GetAllActiveAsync(), t => t.Title == "장보기");
        Assert.Equal(WhenKind.Unscheduled, dateless.WhenKind);
        Assert.Null(dateless.WhenDate);
        Assert.Contains(await store.GetAnytimeAsync(), t => t.Id == dateless.Id);
    }

    [Fact]
    public async Task QuickAddWithBareDate_IsMarkedAllDay_AndRowShowsTheDayAlone()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());

        // A date with no explicit time is an all-day (종일) task.
        vm.QuickAddText = "다음주 금요일 회의";
        await vm.AddCommand.ExecuteAsync(null);

        var listed = Assert.Single(await store.GetAllActiveAsync(), t => t.Title == "회의");
        Assert.Equal(WhenKind.OnDate, listed.WhenKind);
        Assert.NotNull(listed.WhenDate);
        Assert.Null(listed.WhenTime); // the index leaves an all-day row's time column NULL

        var domain = await store.GetAsync<TaskItem>(listed.Id);
        Assert.True(domain!.When.IsAllDay); // carried explicitly on the domain value

        // The list row shows the day alone — no time fragment (which would carry a ':').
        var row = vm.Tasks.Single(r => r.Id == listed.Id);
        Assert.True(row.HasSchedule);
        Assert.DoesNotContain(":", row.Schedule);
    }

    [Fact]
    public async Task DetailOpensAllDay_HidesTime_AndUncheckingSavesATimedWhen()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var task = new TaskItem
        {
            Title = "행사",
            When = ScheduledWhen.AllDay(ZonedDateTime.FromLocal(new DateTime(2026, 6, 24, 0, 0, 0), "UTC")),
        };
        await store.SaveAsync(task);

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        await vm.Detail.OpenAsync(task.Id);

        // An all-day task opens flagged 종일 with the time hidden and no time value.
        Assert.True(vm.Detail.IsWhenAllDay);
        Assert.Null(vm.Detail.WhenTime);
        Assert.False(vm.Detail.ShowWhenTime);

        // Unchecking 종일 reveals the time picker; choosing a time saves a timed (non-all-day) When.
        vm.Detail.IsWhenAllDay = false;
        Assert.True(vm.Detail.ShowWhenTime);
        vm.Detail.SelectedWhenHour = vm.Detail.Hours[9];
        vm.Detail.SelectedWhenMinute = vm.Detail.Minutes[30];
        await vm.Detail.DrainPendingSaveAsync();

        var saved = await store.GetAsync<TaskItem>(task.Id);
        Assert.False(saved!.When.IsAllDay);
        Assert.Equal(new TimeSpan(9, 30, 0), saved.When.Date!.Value.ToLocal().TimeOfDay);
    }

    [Fact]
    public async Task DetailMarkingAllDay_SavesAllDay_AndIndexDropsTheTime()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var task = new TaskItem
        {
            Title = "회의",
            When = ScheduledWhen.On(ZonedDateTime.FromLocal(new DateTime(2026, 6, 24, 15, 0, 0), "UTC")),
        };
        await store.SaveAsync(task);

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        await vm.Detail.OpenAsync(task.Id);
        Assert.False(vm.Detail.IsWhenAllDay);

        // Checking 종일 autosaves the task as all-day.
        vm.Detail.IsWhenAllDay = true;
        await vm.Detail.DrainPendingSaveAsync();

        var saved = await store.GetAsync<TaskItem>(task.Id);
        Assert.True(saved!.When.IsAllDay);

        // The index reflects it as date-only: a day is kept, the time is dropped.
        var listed = Assert.Single(await store.GetAllActiveAsync(), t => t.Id == task.Id);
        Assert.NotNull(listed.WhenDate);
        Assert.Null(listed.WhenTime);
    }

    [Fact]
    public async Task QuickAddWithExplicitSomeday_OnTodayListStaysUnscheduled()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        vm.SetNavigation(new TaskListNavigation(TaskListMode.Today));

        vm.QuickAddText = "언젠가 제주도 한 달 살기";
        await vm.AddCommand.ExecuteAsync(null);

        var task = Assert.Single(await store.GetAnytimeAsync(), t => t.Title == "제주도 한 달 살기");
        Assert.Equal(WhenKind.Unscheduled, task.WhenKind);
        Assert.Null(task.WhenDate);
        Assert.DoesNotContain(await store.GetTodayAsync(), t => t.Id == task.Id);
    }

    [Fact]
    public async Task QuickAddWithRecurrence_UsesAnchorAsFirstWhen()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());

        vm.QuickAddText = "매주 금요일 주간 회고";
        await vm.AddCommand.ExecuteAsync(null);

        var task = Assert.Single(await store.GetUpcomingAsync(), t => t.Title == "주간 회고");
        Assert.Equal(WhenKind.OnDate, task.WhenKind);
        Assert.Equal(new DateOnly(2026, 6, 26), task.WhenDate);
        var saved = await store.GetAsync<TaskItem>(task.Id);
        Assert.NotNull(saved!.Recurrence);
        Assert.Equal(saved.Recurrence.Anchor, saved.When.Date);
    }

    [Fact]
    public async Task QuickAddDateOnlyRecurrence_IsAllDay_ButAnchorKeepsTime()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());

        // No explicit time → the user means "every Friday, all-day", not "every Friday at 00:00".
        vm.QuickAddText = "매주 금요일 주간 회의";
        await vm.AddCommand.ExecuteAsync(null);

        var saved = Assert.Single(await store.GetAllAsync<TaskItem>());
        // The task's own When is all-day (종일): the list shows the day alone, never a 00:00 fragment.
        Assert.True(saved.When.IsAllDay);
        Assert.Equal(new DateOnly(2026, 6, 26), DateOnly.FromDateTime(saved.When.Date!.Value.ToLocal().DateTime));
        // The recurrence anchor still carries a concrete time (00:00) so the engine can evaluate it —
        // the anchor's time and the task's all-day state are decoupled.
        Assert.NotNull(saved.Recurrence);
        Assert.Equal("FREQ=WEEKLY;BYDAY=FR", saved.Recurrence!.Rule);
        Assert.Equal(TimeSpan.Zero, saved.Recurrence.Anchor.ToLocal().TimeOfDay);
    }

    [Fact]
    public async Task QuickAddDateOnlyRecurrence_StaysAllDay_AcrossCompletion()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var recurring = new RecurringTaskService(store);
        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), recurring, clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());

        vm.QuickAddText = "매주 금요일 주간 회의";
        await vm.AddCommand.ExecuteAsync(null);
        var task = Assert.Single(await store.GetAllAsync<TaskItem>());

        // Completing the all-day recurrence advances one cycle and the next instance is still all-day.
        await recurring.CompleteAsync(task.Id, clock.GetUtcNow());

        var advanced = await store.GetAsync<TaskItem>(task.Id);
        Assert.True(advanced!.When.IsAllDay);
        Assert.Equal(new DateOnly(2026, 7, 3), DateOnly.FromDateTime(advanced.When.Date!.Value.ToLocal().DateTime)); // next Friday
    }

    [Fact]
    public async Task QuickAddSomedayWithRecurrence_DropsTheUnanchorableRule()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());

        // The explicit "언젠가" wins the When (Unscheduled), so the parsed recurrence has no date to
        // repeat from. A dateless repeat is meaningless and must not be stored.
        vm.QuickAddText = "언젠가 매주 금요일 주간 회의";
        await vm.AddCommand.ExecuteAsync(null);

        var saved = Assert.Single(await store.GetAllAsync<TaskItem>());
        Assert.Equal(WhenKind.Unscheduled, saved.When.Kind);
        Assert.Null(saved.Recurrence);
    }

    [Fact]
    public async Task QuickAddSubDailyRecurrence_StaysTimed_NotAllDay()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 14, 30, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());

        // A 분 단위 repeat is inherently time-based — it must not be flattened to an all-day (종일) date.
        vm.QuickAddText = "30분마다 스트레칭";
        await vm.AddCommand.ExecuteAsync(null);

        var saved = Assert.Single(await store.GetAllAsync<TaskItem>());
        Assert.False(saved.When.IsAllDay);
        Assert.NotNull(saved.Recurrence);
        Assert.Equal("FREQ=MINUTELY;INTERVAL=30", saved.Recurrence!.Rule);
        Assert.Equal(new TimeSpan(14, 30, 0), saved.When.Date!.Value.ToLocal().TimeOfDay); // anchored on now
    }

    [Fact]
    public async Task DetailClearWhen_SavesAsUnscheduled()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var task = new TaskItem
        {
            Title = "has a date",
            When = ScheduledWhen.On(ZonedDateTime.FromLocal(new DateTime(2026, 6, 23, 9, 0, 0), "UTC")),
        };
        await store.SaveAsync(task);

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        await vm.Detail.OpenAsync(task.Id);

        vm.Detail.ClearWhen();
        Assert.Null(vm.Detail.WhenDate);
        Assert.True(vm.Detail.CanAddWhen);
        Assert.False(vm.Detail.IsWhenEditorVisible);

        await vm.Detail.DrainPendingSaveAsync();

        var saved = await store.GetAsync<TaskItem>(task.Id);
        Assert.Equal(WhenKind.Unscheduled, saved!.When.Kind);
        Assert.False(saved.When.HasDate);
    }

    [Fact]
    public async Task DetailMovingWhen_ReAnchorsRecurrence_EvenWhenRuleUnchanged()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 20, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);

        // 매주 월요일 오전 9시 — anchor and When both Monday 2026-06-22 09:00.
        var monday9 = ZonedDateTime.FromLocal(new DateTime(2026, 6, 22, 9, 0, 0), "UTC");
        var task = new TaskItem
        {
            Title = "주간 회의",
            When = ScheduledWhen.On(monday9),
            Recurrence = new RecurrenceRule("FREQ=WEEKLY", monday9),
        };
        await store.SaveAsync(task);

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        await vm.Detail.OpenAsync(task.Id);

        // User moves the date/time to Tuesday 2026-06-23 15:00 without touching the 반복 selection.
        vm.Detail.WhenDate = new DateTimeOffset(2026, 6, 23, 0, 0, 0, TimeSpan.Zero);
        vm.Detail.SelectedWhenHour = vm.Detail.Hours[15];
        vm.Detail.SelectedWhenMinute = vm.Detail.Minutes[0];
        await vm.Detail.DrainPendingSaveAsync();

        // The rule string is unchanged, but the anchor must follow the new When — otherwise the next
        // occurrence after completion would land back on Monday 09:00, contradicting the edit.
        var saved = await store.GetAsync<TaskItem>(task.Id);
        Assert.NotNull(saved!.Recurrence);
        Assert.Equal("FREQ=WEEKLY", saved.Recurrence!.Rule);
        var anchorLocal = saved.Recurrence.Anchor.ToLocal();
        Assert.Equal(new DateOnly(2026, 6, 23), DateOnly.FromDateTime(anchorLocal.DateTime)); // Tuesday
        Assert.Equal(new TimeSpan(15, 0, 0), anchorLocal.TimeOfDay);
        Assert.Equal(saved.When.Date, saved.Recurrence.Anchor); // anchor tracks the new When exactly
    }

    [Fact]
    public async Task DetailMetadataOnlyEdit_PreservesRecurrenceAnchor()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 20, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);

        var monday9 = ZonedDateTime.FromLocal(new DateTime(2026, 6, 22, 9, 0, 0), "UTC");
        var task = new TaskItem
        {
            Title = "주간 회의",
            When = ScheduledWhen.On(monday9),
            Recurrence = new RecurrenceRule("FREQ=WEEKLY", monday9),
            Priority = Priority.None,
        };
        await store.SaveAsync(task);

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        await vm.Detail.OpenAsync(task.Id);

        // Editing only the priority must leave the recurrence anchor untouched (no silent re-anchoring).
        vm.Detail.SelectedPriority = Priority.P1;
        await vm.Detail.DrainPendingSaveAsync();

        var saved = await store.GetAsync<TaskItem>(task.Id);
        Assert.Equal(Priority.P1, saved!.Priority);
        Assert.NotNull(saved.Recurrence);
        Assert.Equal(monday9, saved.Recurrence!.Anchor); // original anchor preserved verbatim
    }

    [Theory]
    [InlineData(TaskListMode.Today, WhenKind.OnDate)]          // only Today pins an actual day
    [InlineData(TaskListMode.Upcoming, WhenKind.Unscheduled)]  // names no specific date → Unscheduled
    [InlineData(TaskListMode.Anytime, WhenKind.Unscheduled)]
    [InlineData(TaskListMode.AllTasks, WhenKind.Unscheduled)]
    public void QuickAddContextPinsTodayOnly_ElseLeavesUnscheduled(TaskListMode mode, WhenKind kind)
    {
        var now = new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero);
        var result = QuickAddContext.Apply(ScheduledWhen.Unscheduled, whenAssigned: false, mode, now, TimeZoneInfo.Utc);

        Assert.Equal(kind, result.Kind);
        if (kind == WhenKind.OnDate)
            Assert.Equal(new DateOnly(2026, 6, 23), DateOnly.FromDateTime(result.Date!.Value.ToLocal().DateTime));
    }

    [Fact]
    public void QuickAddContextNeverOverridesExplicitParserResult()
    {
        var explicitWhen = ScheduledWhen.On(
            ZonedDateTime.FromLocal(new DateTime(2026, 7, 1, 9, 0, 0), "UTC"));

        var result = QuickAddContext.Apply(
            explicitWhen,
            TaskListMode.Today,
            new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.Equal(explicitWhen, result);
    }

    [Fact]
    public void QuickAddContextDoesNotPinExplicitUnscheduledOnToday()
    {
        var result = QuickAddContext.Apply(
            ScheduledWhen.Unscheduled,
            whenAssigned: true,
            TaskListMode.Today,
            new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.Equal(WhenKind.Unscheduled, result.Kind);
    }

    [Fact]
    public async Task StartupResumesPartiallyAppliedTaskGroupDeletionJournal()
    {
        using var temp = new TempDirectory();
        var options = new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") };
        var project = new TaskGroup { Name = "project" };
        var first = new TaskItem { Title = "first", TaskGroupId = project.Id };
        var second = new TaskItem { Title = "second", TaskGroupId = project.Id };

        await using (var store = await IndexedTaskStore.OpenAsync(options, timeZone: TimeZoneInfo.Utc))
        {
            await store.SaveAsync(project);
            await store.SaveAsync(first);
            await store.SaveAsync(second);

            // Simulate a crash after the durable intent and the first child rewrite.
            first.TaskGroupId = null;
            await store.SaveAsync(first);
        }

        var operationId = Guid.NewGuid();
        var operationDirectory = Path.Combine(temp.Path, "meta", "operations");
        Directory.CreateDirectory(operationDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(operationDirectory, operationId + ".json"),
            $$"""{"id":"{{operationId}}","kind":0,"targetId":"{{project.Id}}","isCompleted":false,"schemaVersion":1}""");

        await using var recovered = await IndexedTaskStore.OpenAsync(options, timeZone: TimeZoneInfo.Utc);

        Assert.Null((await recovered.GetAsync<TaskItem>(first.Id))!.TaskGroupId);
        Assert.Null((await recovered.GetAsync<TaskItem>(second.Id))!.TaskGroupId);
        Assert.True((await recovered.GetAsync<TaskGroup>(project.Id))!.IsDeleted);
        Assert.Empty(await recovered.GetByTaskGroupAsync(project.Id));
        var journal = await File.ReadAllTextAsync(Path.Combine(operationDirectory, operationId + ".json"));
        Assert.Contains("\"isCompleted\": true", journal);
    }

    [Fact]
    public async Task StaleSaveCannotRestoreDeletedContainerOrItsTaskReference()
    {
        using var temp = new TempDirectory();
        var options = new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") };
        await using var store = await IndexedTaskStore.OpenAsync(options, timeZone: TimeZoneInfo.Utc);
        var project = new TaskGroup { Name = "project" };
        var task = new TaskItem { Title = "task", TaskGroupId = project.Id };
        await store.SaveAsync(project);
        await store.SaveAsync(task);

        var staleGroup = await store.GetAsync<TaskGroup>(project.Id);
        var staleTask = await store.GetAsync<TaskItem>(task.Id);
        await store.DeleteAsync<TaskGroup>(project.Id);

        staleTask!.Title = "queued stale edit";
        await store.SaveAsync(staleTask);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(staleGroup!));

        var savedTask = await store.GetAsync<TaskItem>(task.Id);
        Assert.Null(savedTask!.TaskGroupId);
        Assert.True((await store.GetAsync<TaskGroup>(project.Id))!.IsDeleted);
    }

    [Fact]
    public async Task DetailPriorityChange_PersistsImmediatelyWithoutAnExplicitSave()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var task = new TaskItem { Title = "no priority yet", Priority = Priority.None };
        await store.SaveAsync(task);

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        await vm.Detail.OpenAsync(task.Id);

        // Changing the priority is a single selection — it autosaves on the spot, no Save button.
        vm.Detail.SelectedPriority = Priority.P1;
        await vm.Detail.DrainPendingSaveAsync();

        var saved = await store.GetAsync<TaskItem>(task.Id);
        Assert.Equal(Priority.P1, saved!.Priority);
    }

    [Fact]
    public async Task DetailTitle_PersistsOnFocusOutNotPerKeystroke()
    {
        using var temp = new TempDirectory();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var task = new TaskItem { Title = "old title" };
        await store.SaveAsync(task);
        var savedAt = (await store.GetAsync<TaskItem>(task.Id))!.UpdatedAt;

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        await vm.Detail.OpenAsync(task.Id);

        // Typing updates the bound property but does not save — no per-keystroke write.
        clock.Now = new DateTimeOffset(2026, 6, 23, 2, 0, 0, TimeSpan.Zero);
        vm.Detail.Title = "edited title";
        await vm.Detail.DrainPendingSaveAsync();
        var midEdit = await store.GetAsync<TaskItem>(task.Id);
        Assert.Equal("old title", midEdit!.Title);
        Assert.Equal(savedAt, midEdit.UpdatedAt);

        // Focus-out flushes the text edit.
        await vm.Detail.FlushAsync();
        var saved = await store.GetAsync<TaskItem>(task.Id);
        Assert.Equal("edited title", saved!.Title);
    }

    [Fact]
    public async Task OpeningTask_DoesNotSaveWhenNothingChanged()
    {
        using var temp = new TempDirectory();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var task = new TaskItem
        {
            Title = "untouched",
            Priority = Priority.P2,
            TaskGroupId = null,
            When = ScheduledWhen.On(ZonedDateTime.FromLocal(new DateTime(2026, 6, 24, 9, 0, 0), "Korea Standard Time")),
        };
        await store.SaveAsync(task);
        var before = (await store.GetAsync<TaskItem>(task.Id))!.UpdatedAt;

        // If opening saved, the store would re-stamp UpdatedAt with the advanced clock.
        clock.Now = new DateTimeOffset(2026, 6, 23, 5, 0, 0, TimeSpan.Zero);
        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        await vm.Detail.OpenAsync(task.Id);
        await vm.Detail.DrainPendingSaveAsync();

        var after = await store.GetAsync<TaskItem>(task.Id);
        Assert.Equal(before, after!.UpdatedAt);
    }

    [Fact]
    public async Task UntouchedDateAutoSave_PreservesOriginalWhenInstantAndZone()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var originalWhen = ZonedDateTime.FromLocal(new DateTime(2026, 6, 24, 15, 30, 0), "Korea Standard Time");
        var task = new TaskItem { Title = "zoned", When = ScheduledWhen.On(originalWhen) };
        await store.SaveAsync(task);

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        await vm.Detail.OpenAsync(task.Id);

        // An immediate-save change that never touches the date must leave the When untouched, including
        // its exact instant and original time zone (the dirty-check returns the original When).
        vm.Detail.SelectedPriority = Priority.P3;
        await vm.Detail.DrainPendingSaveAsync();

        var saved = await store.GetAsync<TaskItem>(task.Id);
        Assert.Equal(Priority.P3, saved!.Priority);
        Assert.Equal(originalWhen, saved.When.Date);
        Assert.Equal(originalWhen.TimeZoneId, saved.When.Date!.Value.TimeZoneId);
    }

    [Fact]
    public async Task RapidSwitchFromAToB_PersistsEachEditOnItsOwnTask()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var a = new TaskItem { Title = "A", Priority = Priority.None };
        var b = new TaskItem { Title = "B", Priority = Priority.P4 };
        await store.SaveAsync(a);
        await store.SaveAsync(b);

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        await vm.Detail.OpenAsync(a.Id);

        // Edit A's priority (queues an autosave carrying A's snapshot) and immediately switch to B without
        // awaiting that save. The switch must drain A's pending write, and the snapshot must keep it bound
        // to A's values — never B's freshly loaded title/priority.
        vm.Detail.SelectedPriority = Priority.P1;
        await vm.SelectTaskCommand.ExecuteAsync(b.Id);
        await vm.Detail.DrainPendingSaveAsync();

        var savedA = await store.GetAsync<TaskItem>(a.Id);
        var savedB = await store.GetAsync<TaskItem>(b.Id);
        Assert.Equal(Priority.P1, savedA!.Priority);
        Assert.Equal("A", savedA.Title);
        // B was only opened, never edited — its record stays exactly as stored.
        Assert.Equal(Priority.P4, savedB!.Priority);
        Assert.Equal("B", savedB.Title);
    }

    [Fact]
    public async Task DetailTitleEdit_PatchesRowInPlace_PreservingRowInstances()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var a = new TaskItem { Title = "A", SortOrder = "a" };
        var b = new TaskItem { Title = "B", SortOrder = "b" };
        await store.SaveAsync(a);
        await store.SaveAsync(b);

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        await vm.LoadAsync();
        var rowA = vm.Tasks.Single(row => row.Id == a.Id);
        var rowB = vm.Tasks.Single(row => row.Id == b.Id);

        // A title edit changes neither membership nor order, so the refresh must patch the row in place.
        await vm.Detail.OpenAsync(a.Id);
        vm.Detail.Title = "A renamed";
        await vm.Detail.FlushAsync();

        Assert.Equal(2, vm.Tasks.Count);
        Assert.Same(rowA, vm.Tasks.Single(row => row.Id == a.Id)); // same instance, patched not recreated
        Assert.Same(rowB, vm.Tasks.Single(row => row.Id == b.Id)); // untouched neighbor preserved
        Assert.Equal("A renamed", rowA.Title);
    }

    [Fact]
    public async Task DetailPriorityEdit_InFlatView_PatchesRowInPlace()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var task = new TaskItem { Title = "task", Priority = Priority.P3, SortOrder = "a" };
        await store.SaveAsync(task);

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        await vm.LoadAsync();
        var row = Assert.Single(vm.Tasks);

        await vm.Detail.OpenAsync(task.Id);
        vm.Detail.SelectedPriority = Priority.P1;
        await vm.Detail.DrainPendingSaveAsync();

        Assert.Same(row, Assert.Single(vm.Tasks)); // a flat view doesn't sort by priority — same instance
        Assert.Equal(Priority.P1, row.Priority);
        Assert.Equal("P1", row.PriorityCaption);
    }

    [Fact]
    public async Task PriorityView_ChangingPriority_MovesRowToTheCorrectBucket()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var p1 = new TaskItem { Title = "urgent", Priority = Priority.P1, SortOrder = "a" };
        var p2 = new TaskItem { Title = "later", Priority = Priority.P2, SortOrder = "b" };
        await store.SaveAsync(p1);
        await store.SaveAsync(p2);

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        vm.SetNavigation(new TaskListNavigation(TaskListMode.Priority));
        await vm.LoadAsync();
        Assert.Equal(2, vm.PrioritySections.Count); // 매우 중요 + 중요

        // Promote the P2 task to P1: it must leave the 중요 bucket (now empty, so removed) and join 매우 중요.
        await vm.Detail.OpenAsync(p2.Id);
        vm.Detail.SelectedPriority = Priority.P1;
        await vm.Detail.DrainPendingSaveAsync();

        var section = Assert.Single(vm.PrioritySections);
        Assert.Equal("매우 중요", section.Name);
        Assert.Equal(2, section.Tasks.Count);
        Assert.Contains(p1.Id, section.Tasks.Select(row => row.Id));
        Assert.Contains(p2.Id, section.Tasks.Select(row => row.Id));
    }

    [Fact]
    public async Task PriorityView_OmitsUnprioritizedTasks()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var p1 = new TaskItem { Title = "urgent", Priority = Priority.P1, SortOrder = "a" };
        var none = new TaskItem { Title = "unranked", Priority = Priority.None, SortOrder = "b" };
        await store.SaveAsync(p1);
        await store.SaveAsync(none);

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        vm.SetNavigation(new TaskListNavigation(TaskListMode.Priority));
        await vm.LoadAsync();

        // Only the 매우 중요 section appears: unprioritized tasks have no section in this view (the 없음
        // bucket was removed), even though the index still returns them.
        var section = Assert.Single(vm.PrioritySections);
        Assert.Equal("매우 중요", section.Name);
        Assert.Equal(1, section.Count);
        Assert.DoesNotContain(none.Id, vm.PrioritySections.SelectMany(s => s.Tasks).Select(row => row.Id));
    }

    [Fact]
    public void PrioritySection_StartsExpanded_AndTogglesCollapsed()
    {
        var section = new PrioritySectionViewModel("매우 중요");
        Assert.True(section.IsExpanded);                 // sections start expanded, like the sidebar's

        section.ToggleExpandedCommand.Execute(null);
        Assert.False(section.IsExpanded);

        section.ToggleExpandedCommand.Execute(null);
        Assert.True(section.IsExpanded);
    }

    [Fact]
    public async Task TaskGroupList_MovingTaskOut_RemovesOnlyThatRow_PreservingOthers()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var project = new TaskGroup { Name = "P" };
        await store.SaveAsync(project);
        var stay = new TaskItem { Title = "stay", TaskGroupId = project.Id, SortOrder = "a" };
        var leave = new TaskItem { Title = "leave", TaskGroupId = project.Id, SortOrder = "b" };
        await store.SaveAsync(stay);
        await store.SaveAsync(leave);

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        vm.SetNavigation(new TaskListNavigation(TaskListMode.TaskGroup, project.Id));
        await vm.LoadAsync();
        var stayRow = vm.Tasks.Single(row => row.Id == stay.Id);

        await vm.MoveTaskToTaskGroupAsync(leave.Id, null); // moves out of the project, reconciling the list

        var remaining = Assert.Single(vm.Tasks);
        Assert.Same(stayRow, remaining);  // the surviving row keeps its instance
        Assert.Equal(stay.Id, remaining.Id);
    }

    [Fact]
    public async Task QueuedAutoSave_PersistsSnapshot_NotLivePanelValuesChangedAfterward()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var a = new TaskItem { Title = "A", Priority = Priority.None };
        await store.SaveAsync(a);

        // The detail panel reads/writes through the gated store; the list still queries the real index.
        var gated = new GatedTaskStore(store);
        var vm = new TaskListViewModel(gated, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        await vm.Detail.OpenAsync(a.Id);

        // Edit A's priority — this queues an autosave — then stall that save at its store read so we can
        // mutate the panel out from under it, exactly as a fast switch to another task would repopulate it.
        gated.ArmGet(a.Id);
        vm.Detail.SelectedPriority = Priority.P1;
        var pending = vm.Detail.DrainPendingSaveAsync();
        await gated.ReachedGate;

        // The live title now reads as another task's. A save that re-read the live panel would stamp this
        // onto A; a snapshot-based save must persist the title that was on screen when the edit happened.
        vm.Detail.Title = "B-LIVE";

        gated.Release();
        await pending;

        var savedA = await store.GetAsync<TaskItem>(a.Id);
        Assert.Equal(Priority.P1, savedA!.Priority);   // the queued edit persisted
        Assert.Equal("A", savedA.Title);               // the later live edit did NOT leak onto A
    }

    [Fact]
    public async Task TaskChangesAffectingCountsRaiseCountsChanged()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var group = new TaskGroup { Name = "G" };
        await store.SaveAsync(group);

        var notifier = new NavDataChangeNotifier();
        var counts = 0;
        notifier.CountsChanged += (_, _) => counts++;
        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, notifier);
        await vm.LoadCommand.ExecuteAsync(null);

        // Quick-add a task → the sidebar's open-task counts shift, so a counts signal is raised.
        vm.QuickAddText = "buy milk";
        await vm.AddCommand.ExecuteAsync(null);
        Assert.Equal(1, counts);
        var row = Assert.Single(vm.Tasks);

        // Moving it into a group shifts both the group's count and the 그룹 없음 bucket.
        await vm.MoveTaskToTaskGroupAsync(row.Id, group.Id);
        Assert.Equal(2, counts);

        // Completing it drops it out of the open-task counts.
        row.SetCompletedSilently(true);
        await vm.ToggleCompleteCommand.ExecuteAsync(row);
        Assert.Equal(3, counts);

        // Deleting it likewise changes the counts.
        await vm.DeleteTaskCommand.ExecuteAsync(row.Id);
        Assert.Equal(4, counts);
    }

    /// <summary>Wraps a real store and blocks the first <c>GetAsync&lt;TaskItem&gt;</c> for an armed id until
    /// released, so a test can hold an autosave in flight and mutate the panel underneath it.</summary>
    private sealed class GatedTaskStore(ITaskStore inner) : ITaskStore
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Guid? _gateId;

        public Task ReachedGate => _reached.Task;
        public void ArmGet(Guid id) => _gateId = id;
        public void Release() => _release.TrySetResult();

        public async Task<T?> GetAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : RecordBase
        {
            if (_gateId == id && typeof(T) == typeof(TaskItem))
            {
                _gateId = null; // gate only the first matching read
                _reached.TrySetResult();
                await _release.Task;
            }
            return await inner.GetAsync<T>(id, cancellationToken);
        }

        public Task<IReadOnlyList<T>> GetAllAsync<T>(CancellationToken cancellationToken = default) where T : RecordBase
            => inner.GetAllAsync<T>(cancellationToken);
        public Task SaveAsync<T>(T record, CancellationToken cancellationToken = default) where T : RecordBase
            => inner.SaveAsync(record, cancellationToken);
        public Task DeleteAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : RecordBase
            => inner.DeleteAsync<T>(id, cancellationToken);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cue-vm-{Guid.NewGuid():N}");
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
