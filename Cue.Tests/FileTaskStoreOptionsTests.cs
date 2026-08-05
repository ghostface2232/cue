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

    /// <summary>
    /// The index must never be co-located with the data root by default. The default root is Documents,
    /// which Windows commonly redirects into OneDrive — a single database file rewritten on every save is
    /// exactly the monolithic synced unit the invariants forbid, and it is the first thing to break when
    /// the data root itself becomes a cloud folder.
    /// </summary>
    [Fact]
    public void CreateDefault_PinsIndexToLocalAppData_NotTheDataRoot()
    {
        var options = FileTaskStoreOptions.CreateDefault();

        Assert.NotNull(options.IndexPath);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.StartsWith(
            Path.Combine(localAppData, "Cue") + Path.DirectorySeparatorChar,
            options.IndexPath!,
            StringComparison.Ordinal);
        Assert.False(
            options.IndexPath!.StartsWith(options.RootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            $"Index '{options.IndexPath}' must not live inside the data root '{options.RootPath}'.");
    }

    /// <summary>Two data roots on one machine each need their own database rather than fighting over one,
    /// and the same root must resolve to the same file on every launch (so the hash cannot be
    /// <c>string.GetHashCode</c>, which .NET randomizes per process).</summary>
    [Fact]
    public void DefaultIndexPath_IsPerRoot_AndStable()
    {
        var first = FileTaskStoreOptions.DefaultIndexPath(Path.Combine("C:", "data", "cue-a"));
        var second = FileTaskStoreOptions.DefaultIndexPath(Path.Combine("C:", "data", "cue-b"));

        Assert.NotEqual(first, second);
        Assert.Equal(first, FileTaskStoreOptions.DefaultIndexPath(Path.Combine("C:", "data", "cue-a")));
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cue"),
            Path.GetDirectoryName(first));
    }

    /// <summary>A trailing separator and a case difference are the same Windows path, so they must not
    /// produce two databases for one data root.</summary>
    [Fact]
    public void DefaultIndexPath_NormalizesCaseAndTrailingSeparator()
    {
        var plain = FileTaskStoreOptions.DefaultIndexPath(Path.Combine("C:", "Data", "Cue"));
        var trailing = FileTaskStoreOptions.DefaultIndexPath(Path.Combine("C:", "Data", "Cue") + Path.DirectorySeparatorChar);
        var lowercase = FileTaskStoreOptions.DefaultIndexPath(Path.Combine("C:", "data", "cue"));

        Assert.Equal(plain, trailing);
        Assert.Equal(plain, lowercase);
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
