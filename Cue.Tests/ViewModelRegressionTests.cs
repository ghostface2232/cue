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
    public async Task CompletionKeepsRowVisibleAndDimmedAcrossReloads()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var task = new TaskItem { Title = "stay visible" };
        await store.SaveAsync(task);

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), new ReorderService(store), new RecurringTaskService(store), clock, TimeZoneInfo.Utc, new NavDataChangeNotifier());
        await vm.LoadCommand.ExecuteAsync(null);
        var row = Assert.Single(vm.Tasks);
        row.SetCompletedSilently(true);
        await vm.ToggleCompleteCommand.ExecuteAsync(row);

        Assert.Same(row, Assert.Single(vm.Tasks));
        Assert.Equal(0.48, row.VisualOpacity);
        Assert.True((await store.GetAsync<TaskItem>(task.Id))!.IsCompleted);

        // Completed items stay in the list (dimmed) across reloads instead of vanishing.
        await vm.LoadCommand.ExecuteAsync(null);
        var reloaded = Assert.Single(vm.Tasks);
        Assert.True(reloaded.IsCompleted);
        Assert.Equal(0.48, reloaded.VisualOpacity);
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
    public async Task TimelineNowLineUsesTimeWithinTheCurrentDay()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);

        var vm = new TimelineViewModel(
            store,
            store,
            new ReorderService(store),
            clock,
            TimeZoneInfo.Utc,
            new NavDataChangeNotifier());
        await vm.LoadCommand.ExecuteAsync(null);

        var expected = ((23 - 1) * vm.DayWidth) + (vm.DayWidth / 2);
        Assert.True(vm.HasTodayInRange);
        Assert.Equal(expected, vm.TodayLineOffset, precision: 6);
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
        Assert.Equal(2, vm.Groups.Count); // 매우 중요 + 중요

        // Promote the P2 task to P1: it must leave the 중요 bucket (now empty, so removed) and join 매우 중요.
        await vm.Detail.OpenAsync(p2.Id);
        vm.Detail.SelectedPriority = Priority.P1;
        await vm.Detail.DrainPendingSaveAsync();

        var group = Assert.Single(vm.Groups);
        Assert.Equal("매우 중요", group.Name);
        Assert.Equal(2, group.Tasks.Count);
        Assert.Contains(p1.Id, group.Tasks.Select(row => row.Id));
        Assert.Contains(p2.Id, group.Tasks.Select(row => row.Id));
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
