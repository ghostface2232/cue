using Cue.Domain;
using Cue.Parsing;
using Cue.Storage;
using Cue.ViewModels;

namespace Cue.Tests;

public sealed class ViewModelRegressionTests
{
    [Fact]
    public async Task DetailSavePreservesWhenAndDeadlineTimes()
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
            Deadline = ZonedDateTime.FromLocal(new DateTime(2026, 6, 25, 18, 45, 0), "UTC"),
        };
        await store.SaveAsync(task);

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), clock, TimeZoneInfo.Utc);
        await vm.Detail.OpenAsync(task.Id);
        await vm.Detail.SaveCommand.ExecuteAsync(null);

        var saved = await store.GetAsync<TaskItem>(task.Id);
        Assert.NotNull(saved);
        Assert.Equal(new TimeSpan(15, 30, 0), saved.When.Date!.Value.ToLocal().TimeOfDay);
        Assert.Equal(new TimeSpan(18, 45, 0), saved.Deadline!.Value.ToLocal().TimeOfDay);
    }

    [Fact]
    public async Task DetailSavePreservesTodayEveningTimeAndFlag()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var task = new TaskItem
        {
            Title = "evening",
            When = ScheduledWhen.On(
                ZonedDateTime.FromLocal(new DateTime(2026, 6, 23, 20, 10, 0), "UTC"),
                evening: true),
        };
        await store.SaveAsync(task);

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), clock, TimeZoneInfo.Utc);
        await vm.Detail.OpenAsync(task.Id);
        await vm.Detail.SaveCommand.ExecuteAsync(null);

        var saved = await store.GetAsync<TaskItem>(task.Id);
        Assert.NotNull(saved);
        Assert.True(saved.When.IsEvening);
        Assert.Equal(new TimeSpan(20, 10, 0), saved.When.Date!.Value.ToLocal().TimeOfDay);
    }

    [Fact]
    public async Task DetailSaveWithoutDateEditsPreservesOriginalTimeZonesAndInstants()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var originalWhen = ZonedDateTime.FromLocal(new DateTime(2026, 6, 24, 15, 30, 0), "Korea Standard Time");
        var originalDeadline = ZonedDateTime.FromLocal(new DateTime(2026, 6, 25, 18, 45, 0), "Korea Standard Time");
        var task = new TaskItem { Title = "zoned", When = ScheduledWhen.On(originalWhen), Deadline = originalDeadline };
        await store.SaveAsync(task);

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), clock, TimeZoneInfo.Utc);
        await vm.Detail.OpenAsync(task.Id);
        vm.Detail.Title = "title only";
        await vm.Detail.SaveCommand.ExecuteAsync(null);

        var saved = await store.GetAsync<TaskItem>(task.Id);
        Assert.NotNull(saved);
        Assert.Equal(originalWhen, saved.When.Date);
        Assert.Equal(originalDeadline, saved.Deadline);
    }

    [Fact]
    public async Task CompletionKeepsRowVisibleAndDimmedUntilNextReload()
    {
        using var temp = new TempDirectory();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero));
        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") },
            clock,
            TimeZoneInfo.Utc);
        var task = new TaskItem { Title = "stay visible" };
        await store.SaveAsync(task);

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), clock, TimeZoneInfo.Utc);
        await vm.LoadCommand.ExecuteAsync(null);
        var row = Assert.Single(vm.Tasks);
        row.SetCompletedSilently(true);
        await vm.ToggleCompleteCommand.ExecuteAsync(row);

        Assert.Same(row, Assert.Single(vm.Tasks));
        Assert.Equal(0.48, row.VisualOpacity);
        Assert.True((await store.GetAsync<TaskItem>(task.Id))!.IsCompleted);

        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Empty(vm.Tasks);
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

        var vm = new TaskListViewModel(store, store, new KoreanDateParser(), clock, TimeZoneInfo.Utc);
        await vm.Detail.OpenAsync(task.Id);
        vm.Detail.SelectedWhenHour = vm.Detail.Hours[13];
        vm.Detail.SelectedWhenMinute = vm.Detail.Minutes[5];
        await vm.Detail.SaveCommand.ExecuteAsync(null);

        var saved = await store.GetAsync<TaskItem>(task.Id);
        Assert.Equal(new TimeSpan(13, 5, 0), saved!.When.Date!.Value.ToLocal().TimeOfDay);
    }

    [Theory]
    [InlineData(TaskListMode.Today, WhenKind.OnDate, 0)]
    [InlineData(TaskListMode.Upcoming, WhenKind.OnDate, 1)]
    [InlineData(TaskListMode.Someday, WhenKind.SomeDay, 0)]
    [InlineData(TaskListMode.Anytime, WhenKind.Unscheduled, 0)]
    public void QuickAddContextKeepsDatelessTaskInItsTimeView(TaskListMode mode, WhenKind kind, int dayOffset)
    {
        var now = new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero);
        var result = QuickAddContext.Apply(ScheduledWhen.Unscheduled, mode, now, TimeZoneInfo.Utc);

        Assert.Equal(kind, result.Kind);
        if (kind == WhenKind.OnDate)
            Assert.Equal(new DateOnly(2026, 6, 23).AddDays(dayOffset), DateOnly.FromDateTime(result.Date!.Value.ToLocal().DateTime));
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
    public async Task StartupResumesPartiallyAppliedProjectDeletionJournal()
    {
        using var temp = new TempDirectory();
        var options = new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") };
        var project = new Project { Name = "project" };
        var first = new TaskItem { Title = "first", ProjectId = project.Id };
        var second = new TaskItem { Title = "second", ProjectId = project.Id };

        await using (var store = await IndexedTaskStore.OpenAsync(options, timeZone: TimeZoneInfo.Utc))
        {
            await store.SaveAsync(project);
            await store.SaveAsync(first);
            await store.SaveAsync(second);

            // Simulate a crash after the durable intent and the first child rewrite.
            first.ProjectId = null;
            await store.SaveAsync(first);
        }

        var operationId = Guid.NewGuid();
        var operationDirectory = Path.Combine(temp.Path, "meta", "operations");
        Directory.CreateDirectory(operationDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(operationDirectory, operationId + ".json"),
            $$"""{"id":"{{operationId}}","kind":0,"targetId":"{{project.Id}}","isCompleted":false,"schemaVersion":1}""");

        await using var recovered = await IndexedTaskStore.OpenAsync(options, timeZone: TimeZoneInfo.Utc);

        Assert.Null((await recovered.GetAsync<TaskItem>(first.Id))!.ProjectId);
        Assert.Null((await recovered.GetAsync<TaskItem>(second.Id))!.ProjectId);
        Assert.True((await recovered.GetAsync<Project>(project.Id))!.IsDeleted);
        Assert.Empty(await recovered.GetByProjectAsync(project.Id));
        var journal = await File.ReadAllTextAsync(Path.Combine(operationDirectory, operationId + ".json"));
        Assert.Contains("\"isCompleted\": true", journal);
    }

    [Fact]
    public async Task StaleSaveCannotRestoreDeletedContainerOrItsTaskReference()
    {
        using var temp = new TempDirectory();
        var options = new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") };
        await using var store = await IndexedTaskStore.OpenAsync(options, timeZone: TimeZoneInfo.Utc);
        var project = new Project { Name = "project" };
        var task = new TaskItem { Title = "task", ProjectId = project.Id };
        await store.SaveAsync(project);
        await store.SaveAsync(task);

        var staleProject = await store.GetAsync<Project>(project.Id);
        var staleTask = await store.GetAsync<TaskItem>(task.Id);
        await store.DeleteAsync<Project>(project.Id);

        staleTask!.Title = "queued stale edit";
        await store.SaveAsync(staleTask);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(staleProject!));

        var savedTask = await store.GetAsync<TaskItem>(task.Id);
        Assert.Null(savedTask!.ProjectId);
        Assert.Null(savedTask.SectionId);
        Assert.True((await store.GetAsync<Project>(project.Id))!.IsDeleted);
    }

    [Fact]
    public async Task QueuedSectionCannotAttachToDeletedProject()
    {
        using var temp = new TempDirectory();
        var options = new FileTaskStoreOptions { RootPath = temp.Path, IndexPath = Path.Combine(temp.Path, "index.db") };
        await using var store = await IndexedTaskStore.OpenAsync(options, timeZone: TimeZoneInfo.Utc);
        var project = new Project { Name = "project" };
        await store.SaveAsync(project);
        var queuedSection = new Section { Name = "late section", ProjectId = project.Id };

        await store.DeleteAsync<Project>(project.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(queuedSection));
        Assert.Empty(await store.GetSectionsByProjectAsync(project.Id));
        Assert.Null(await store.GetAsync<Section>(queuedSection.Id));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cue-vm-{Guid.NewGuid():N}");
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
