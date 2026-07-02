using Cue.Domain;
using Cue.Storage;

namespace Cue.Tests;

public sealed class FileTaskStoreOptionsTests : IDisposable
{
    private readonly List<string> _roots = new();

    [Fact]
    public void CreateDefault_ResolvesRootUnderDocuments_NotAppData()
    {
        var options = FileTaskStoreOptions.CreateDefault();

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Assert.Equal(Path.Combine(documents, "Cue"), options.RootPath);

        // Data-root distribution invariant: the root must never live under %USERPROFILE%\AppData,
        // the MSIX virtualization scope. AppData is the parent of the Local/Roaming folders.
        var appData = Path.GetDirectoryName(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))!;
        Assert.False(
            options.RootPath.StartsWith(appData, StringComparison.OrdinalIgnoreCase),
            $"Data root '{options.RootPath}' must not live under AppData '{appData}'.");
    }

    [Fact]
    public void CreateDefault_HonorsCustomFolderName()
    {
        var options = FileTaskStoreOptions.CreateDefault("Cue-Beta");

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Assert.Equal(Path.Combine(documents, "Cue-Beta"), options.RootPath);
    }

    [Fact]
    public async Task OpenAsync_CreatesRoot_AndInitializes_WhenRootMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "cue-open-tests", Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        Assert.False(Directory.Exists(root));

        await using var store = await IndexedTaskStore.OpenAsync(
            new FileTaskStoreOptions { RootPath = root });

        // The store creates the root on open (the co-located index database does).
        Assert.True(Directory.Exists(root));

        // A fresh, empty root lists cleanly rather than throwing.
        Assert.Empty(await store.GetAllAsync<TaskItem>());
        Assert.Empty(await store.GetAllActiveAsync());

        // And it is immediately writable and readable.
        var task = new TaskItem { Title = "첫 할 일" };
        await store.SaveAsync(task);
        var loaded = await store.GetAsync<TaskItem>(task.Id);
        Assert.NotNull(loaded);
        Assert.Equal("첫 할 일", loaded!.Title);
    }

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }
}
