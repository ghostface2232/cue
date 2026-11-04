using System.Diagnostics;
using Cue.Domain;
using Cue.Services;
using Cue.Storage;

namespace Cue.Tests;

public sealed class DiagnosticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cue-diagnostics-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup of a temp folder */ }
    }

    private string LogPath => Path.Combine(_root, "logs", "cue.log");

    [Fact]
    public void Listener_CreatesTheFolderAndAppendsLines()
    {
        using var listener = new RollingFileTraceListener(LogPath);

        listener.WriteLine("첫째 줄");
        listener.WriteLine("둘째 줄");

        var lines = File.ReadAllLines(LogPath);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("첫째 줄", lines[0]);
        Assert.EndsWith("둘째 줄", lines[1]);
    }

    [Fact]
    public void Listener_KeepsOnlyACappedNumberOfFiles()
    {
        using var listener = new RollingFileTraceListener(LogPath, maxBytes: 100);

        for (var i = 0; i < 40; i++)
            listener.WriteLine($"줄 {i} " + new string('x', 40));

        var files = Directory.GetFiles(Path.GetDirectoryName(LogPath)!).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(new[] { "cue.1.log", "cue.2.log", "cue.log" }, files);
        Assert.Contains("줄 39", File.ReadAllText(LogPath));
    }

    [Fact]
    public void Listener_NeverThrows_WhenItCannotWrite()
    {
        Directory.CreateDirectory(_root);
        var blockedByFile = Path.Combine(_root, "not-a-folder");
        File.WriteAllText(blockedByFile, "");
        using var listener = new RollingFileTraceListener(Path.Combine(blockedByFile, "cue.log"));

        listener.WriteLine("어디에도 못 쓰는 줄");
    }

    [Fact]
    public void Listener_SharesTheFileWithAnotherProcessStillWriting()
    {
        using var first = new RollingFileTraceListener(LogPath);
        using var second = new RollingFileTraceListener(LogPath);

        Parallel.For(0, 100, i => (i % 2 == 0 ? first : second).WriteLine($"줄 {i}"));

        Assert.Equal(100, File.ReadAllLines(LogPath).Length);
    }

    [Fact]
    public async Task Store_ReportsSkippedFiles_ByReason_AndTracesThem()
    {
        var captured = new CapturingListener();
        Trace.Listeners.Add(captured);
        try
        {
            await using var seed = await IndexedTaskStore.OpenAsync(new FileTaskStoreOptions { RootPath = _root });
            var healthy = new TaskItem { Title = "멀쩡한 일" };
            await seed.SaveAsync(healthy);
            await seed.DisposeAsync();

            var tasks = Path.Combine(_root, "tasks");
            var unreadable = Path.Combine(tasks, Guid.NewGuid() + ".json");
            var conflictCopy = Path.Combine(tasks, healthy.Id + "-MYPC.json");
            await File.WriteAllTextAsync(unreadable, "{ not json");
            await File.WriteAllTextAsync(conflictCopy, "{}");

            await using var store = await IndexedTaskStore.OpenAsync(new FileTaskStoreOptions { RootPath = _root });

            Assert.Equal(
                new[]
                {
                    new SkippedFile(conflictCopy, SkippedFileReason.NotARecord),
                    new SkippedFile(unreadable, SkippedFileReason.Unreadable),
                }.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase),
                store.SkippedFiles);
            Assert.Contains(captured.Lines, line => line.Contains(unreadable));
            Assert.Contains(captured.Lines, line => line.Contains(conflictCopy));

            // The file isn't rewritten, and once repaired or removed it stops being reported.
            File.Delete(conflictCopy);
            File.Delete(unreadable);
            Assert.Empty(store.SkippedFiles);
        }
        finally
        {
            Trace.Listeners.Remove(captured);
        }
    }

    [Fact]
    public async Task Store_StopsReportingAFile_OnceItReadsAgain()
    {
        await using var store = await IndexedTaskStore.OpenAsync(new FileTaskStoreOptions { RootPath = _root });
        var task = new TaskItem { Title = "잠시 못 읽는 일" };
        await store.SaveAsync(task);
        var path = Path.Combine(_root, "tasks", task.Id + ".json");
        var good = await File.ReadAllTextAsync(path);

        await File.WriteAllTextAsync(path, "{ not json");
        Assert.Null(await store.GetAsync<TaskItem>(task.Id));
        Assert.Single(store.SkippedFiles);

        await File.WriteAllTextAsync(path, good);
        Assert.NotNull(await store.GetAsync<TaskItem>(task.Id));
        Assert.Empty(store.SkippedFiles);
    }

    private sealed class CapturingListener : TraceListener
    {
        private readonly List<string> _lines = new();

        public IReadOnlyList<string> Lines
        {
            get { lock (_lines) return _lines.ToArray(); }
        }

        public override void Write(string? message) => WriteLine(message);

        public override void WriteLine(string? message)
        {
            lock (_lines) _lines.Add(message ?? string.Empty);
        }
    }
}
