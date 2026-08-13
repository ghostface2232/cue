using Cue.Domain;
using Cue.Services;
using Cue.Storage;
using Cue.ViewModels;

namespace Cue.Tests;

public sealed class TaskOpenRouteResolverTests
{
    [Fact]
    public async Task Resolve_OpenTask_RoutesToAllTasks()
    {
        using var temp = new TempDirectory();
        await using var store = await OpenStoreAsync(temp.Path);
        var task = new TaskItem { Title = "열린 작업" };
        await store.SaveAsync(task);

        var route = await new TaskOpenRouteResolver(store).ResolveAsync(task.Id);

        Assert.NotNull(route);
        Assert.Equal(task.Id, route.TaskId);
        Assert.Equal(TaskListMode.AllTasks, route.Navigation.Mode);
    }

    [Fact]
    public async Task Resolve_CompletedTask_RoutesToLogbook()
    {
        using var temp = new TempDirectory();
        await using var store = await OpenStoreAsync(temp.Path);
        var task = new TaskItem
        {
            Title = "완료 작업",
            CompletedAt = new DateTimeOffset(2026, 8, 13, 1, 0, 0, TimeSpan.Zero),
        };
        await store.SaveAsync(task);

        var route = await new TaskOpenRouteResolver(store).ResolveAsync(task.Id);

        Assert.NotNull(route);
        Assert.Equal(TaskListMode.Logbook, route.Navigation.Mode);
    }

    [Fact]
    public async Task Resolve_MissingOrDeletedTask_ReturnsNoRoute()
    {
        using var temp = new TempDirectory();
        await using var store = await OpenStoreAsync(temp.Path);
        var task = new TaskItem { Title = "삭제 작업" };
        await store.SaveAsync(task);
        await store.DeleteAsync<TaskItem>(task.Id);
        var resolver = new TaskOpenRouteResolver(store);

        Assert.Null(await resolver.ResolveAsync(Guid.NewGuid()));
        Assert.Null(await resolver.ResolveAsync(task.Id));
        Assert.Null(await resolver.ResolveAsync(Guid.Empty));
    }

    private static Task<IndexedTaskStore> OpenStoreAsync(string rootPath)
        => IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = rootPath, IndexPath = Path.Combine(rootPath, "index.db") },
            TimeProvider.System,
            TimeZoneInfo.Utc);

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "Cue.Tests", Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best-effort test cleanup */ }
        }
    }
}
