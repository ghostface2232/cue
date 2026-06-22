using Cue.Domain;
using Cue.Storage.Index;

namespace Cue.Storage;

/// <summary>
/// The app's single data seam: an <see cref="ITaskStore"/> whose files are the source of truth,
/// paired with a SQLite query index that every list read goes through. It guarantees the two never
/// drift — there is exactly one write path, and it updates the file <i>and</i> the index together.
/// </summary>
/// <remarks>
/// On <see cref="InitializeAsync"/> (app start) the index is rebuilt in full from the task files,
/// so the database is disposable: delete it and the next launch reconstructs it. Thereafter each
/// <see cref="SaveAsync"/>/<see cref="DeleteAsync"/> writes the file first, then reflects just that
/// one record into the index — no code path touches a file without the index following.
/// <para>
/// Reads split by responsibility: by-id and "give me every record" fetches (<see cref="GetAsync"/>,
/// <see cref="GetAllAsync"/>) come from the files, while all <i>list</i> queries (the
/// <see cref="ITaskIndex"/> surface) are answered from SQLite and never scan the folder.
/// </para>
/// </remarks>
public sealed class IndexedTaskStore : ITaskStore, ITaskIndex, IAsyncDisposable
{
    private readonly ITaskStore _files;
    private readonly SqliteTaskIndex _index;

    public IndexedTaskStore(ITaskStore files, SqliteTaskIndex index)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _index = index ?? throw new ArgumentNullException(nameof(index));
    }

    /// <summary>
    /// Wires a <see cref="FileTaskStore"/> over <paramref name="options"/> to a SQLite index at
    /// <c>{root}/index.db</c>, then rebuilds the index from the files. The single entry point an app
    /// uses at startup. <paramref name="timeProvider"/>/<paramref name="timeZone"/> define the
    /// "today" the time-axis views compare against.
    /// </summary>
    public static async Task<IndexedTaskStore> OpenAsync(
        FileTaskStoreOptions options,
        TimeProvider? timeProvider = null,
        TimeZoneInfo? timeZone = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var files = new FileTaskStore(options, timeProvider);
        var index = new SqliteTaskIndex(Path.Combine(options.RootPath, "index.db"), timeProvider, timeZone);
        var store = new IndexedTaskStore(files, index);
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return store;
    }

    /// <summary>
    /// Rebuilds the index from the full set of task files. Run this at startup; it makes the index
    /// match the files exactly, recovering completely even if the database was deleted or is stale.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var tasks = await _files.GetAllAsync<TaskItem>(cancellationToken).ConfigureAwait(false);
        await _index.RebuildAsync(tasks, cancellationToken).ConfigureAwait(false);
    }

    // ---- Write path: file first, then index (always both) --------------------

    public async Task SaveAsync<T>(T record, CancellationToken cancellationToken = default) where T : RecordBase
    {
        await _files.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        if (record is TaskItem task)
            await _index.ReflectAsync(task, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : RecordBase
    {
        await _files.DeleteAsync<T>(id, cancellationToken).ConfigureAwait(false);
        if (typeof(T) != typeof(TaskItem))
            return;

        // Re-read the now-tombstoned file and mirror it into the index, so the row carries the same
        // deleted_at and drops out of every default query. Same reflect path as a normal save.
        var tombstone = await _files.GetAsync<TaskItem>(id, cancellationToken).ConfigureAwait(false);
        if (tombstone is not null)
            await _index.ReflectAsync(tombstone, cancellationToken).ConfigureAwait(false);
    }

    // ---- By-id / full reads come from the files (source of truth) ------------

    public Task<IReadOnlyList<T>> GetAllAsync<T>(CancellationToken cancellationToken = default) where T : RecordBase
        => _files.GetAllAsync<T>(cancellationToken);

    public Task<T?> GetAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : RecordBase
        => _files.GetAsync<T>(id, cancellationToken);

    // ---- List reads come from the index --------------------------------------

    public Task<IReadOnlyList<TaskListItem>> GetInboxAsync(CancellationToken cancellationToken = default)
        => _index.GetInboxAsync(cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetByProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
        => _index.GetByProjectAsync(projectId, cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetBySectionAsync(Guid sectionId, CancellationToken cancellationToken = default)
        => _index.GetBySectionAsync(sectionId, cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetByLabelAsync(Guid labelId, CancellationToken cancellationToken = default)
        => _index.GetByLabelAsync(labelId, cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetTodayAsync(CancellationToken cancellationToken = default)
        => _index.GetTodayAsync(cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetThisEveningAsync(CancellationToken cancellationToken = default)
        => _index.GetThisEveningAsync(cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetUpcomingAsync(CancellationToken cancellationToken = default)
        => _index.GetUpcomingAsync(cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetAnytimeAsync(CancellationToken cancellationToken = default)
        => _index.GetAnytimeAsync(cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetSomedayAsync(CancellationToken cancellationToken = default)
        => _index.GetSomedayAsync(cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetLogbookAsync(CancellationToken cancellationToken = default)
        => _index.GetLogbookAsync(cancellationToken);

    public ValueTask DisposeAsync() => _index.DisposeAsync();
}
