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
    /// Wires a <see cref="FileTaskStore"/> over <paramref name="options"/> to a SQLite index, then
    /// rebuilds the index from the files. The single entry point an app uses at startup. The index
    /// lives at <see cref="FileTaskStoreOptions.IndexPath"/> when set, else co-located at
    /// <c>{root}/index.db</c> — keep it local and off any synced data root, since it is a per-device
    /// cache. <paramref name="timeProvider"/>/<paramref name="timeZone"/> define the "today" the
    /// time-axis views compare against.
    /// </summary>
    public static async Task<IndexedTaskStore> OpenAsync(
        FileTaskStoreOptions options,
        TimeProvider? timeProvider = null,
        TimeZoneInfo? timeZone = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var indexPath = options.IndexPath ?? Path.Combine(options.RootPath, "index.db");
        var files = new FileTaskStore(options, timeProvider);
        var index = new SqliteTaskIndex(indexPath, timeProvider, timeZone);
        var store = new IndexedTaskStore(files, index);
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return store;
    }

    /// <summary>
    /// Rebuilds the index from the full set of record files. Run this at startup; it makes the index
    /// match the files exactly, recovering completely even if the database was deleted or is stale.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var tasks = await _files.GetAllAsync<TaskItem>(cancellationToken).ConfigureAwait(false);
        var projects = await _files.GetAllAsync<Project>(cancellationToken).ConfigureAwait(false);
        var sections = await _files.GetAllAsync<Section>(cancellationToken).ConfigureAwait(false);
        var labels = await _files.GetAllAsync<Label>(cancellationToken).ConfigureAwait(false);
        await _index.RebuildAsync(tasks, projects, sections, labels, cancellationToken).ConfigureAwait(false);
    }

    // ---- Write path: file first, then index (always both) --------------------

    public async Task SaveAsync<T>(T record, CancellationToken cancellationToken = default) where T : RecordBase
    {
        await _files.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        await ReflectAsync(record, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : RecordBase
    {
        if (typeof(T) == typeof(Project))
            await DeleteProjectAsync(id, cancellationToken).ConfigureAwait(false);
        else if (typeof(T) == typeof(Section))
            await DeleteSectionAsync(id, cancellationToken).ConfigureAwait(false);
        else if (typeof(T) == typeof(Label))
            await DeleteLabelAsync(id, cancellationToken).ConfigureAwait(false);
        else
            await SoftDeleteAndReflectAsync<T>(id, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReflectAsync<T>(T record, CancellationToken cancellationToken) where T : RecordBase
    {
        switch (record)
        {
            case TaskItem task: await _index.ReflectAsync(task, cancellationToken).ConfigureAwait(false); break;
            case Project project: await _index.ReflectAsync(project, cancellationToken).ConfigureAwait(false); break;
            case Section section: await _index.ReflectAsync(section, cancellationToken).ConfigureAwait(false); break;
            case Label label: await _index.ReflectAsync(label, cancellationToken).ConfigureAwait(false); break;
            default: throw new NotSupportedException($"Index reflection is not defined for {record.GetType().Name}.");
        }
    }

    private async Task SoftDeleteAndReflectAsync<T>(Guid id, CancellationToken cancellationToken) where T : RecordBase
    {
        await _files.DeleteAsync<T>(id, cancellationToken).ConfigureAwait(false);
        var tombstone = await _files.GetAsync<T>(id, cancellationToken).ConfigureAwait(false);
        if (tombstone is not null)
            await ReflectAsync(tombstone, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        // Foundation deletion policy: preserve user work by moving every live task to Inbox rather
        // than cascading task tombstones. Both container ids are cleared because project deletion
        // also tombstones its child sections. This is intentionally the least destructive default.
        foreach (var taskId in await _index.GetTaskIdsByProjectAsync(projectId, cancellationToken).ConfigureAwait(false))
        {
            var task = await _files.GetAsync<TaskItem>(taskId, cancellationToken).ConfigureAwait(false);
            if (task is null) continue;
            task.ProjectId = null;
            task.SectionId = null;
            await SaveAsync(task, cancellationToken).ConfigureAwait(false);
        }

        foreach (var sectionId in await _index.GetSectionIdsByProjectAsync(projectId, cancellationToken).ConfigureAwait(false))
            await SoftDeleteAndReflectAsync<Section>(sectionId, cancellationToken).ConfigureAwait(false);

        await SoftDeleteAndReflectAsync<Project>(projectId, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteSectionAsync(Guid sectionId, CancellationToken cancellationToken)
    {
        // Same preservation policy as project deletion: removing the grouping must never delete the
        // work inside it. Move affected tasks to the unclassified Inbox (clear both references).
        foreach (var taskId in await _index.GetTaskIdsBySectionAsync(sectionId, cancellationToken).ConfigureAwait(false))
        {
            var task = await _files.GetAsync<TaskItem>(taskId, cancellationToken).ConfigureAwait(false);
            if (task is null) continue;
            task.ProjectId = null;
            task.SectionId = null;
            await SaveAsync(task, cancellationToken).ConfigureAwait(false);
        }

        await SoftDeleteAndReflectAsync<Section>(sectionId, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteLabelAsync(Guid labelId, CancellationToken cancellationToken)
    {
        // Labels are cross-cutting metadata. Delete only the label record and remove its references;
        // never delete or relocate a task merely because one of its labels was removed.
        foreach (var taskId in await _index.GetTaskIdsByLabelAsync(labelId, cancellationToken).ConfigureAwait(false))
        {
            var task = await _files.GetAsync<TaskItem>(taskId, cancellationToken).ConfigureAwait(false);
            if (task is null || task.LabelIds.RemoveAll(id => id == labelId) == 0) continue;
            await SaveAsync(task, cancellationToken).ConfigureAwait(false);
        }

        await SoftDeleteAndReflectAsync<Label>(labelId, cancellationToken).ConfigureAwait(false);
    }

    // ---- By-id / full reads come from the files (source of truth) ------------

    public Task<IReadOnlyList<T>> GetAllAsync<T>(CancellationToken cancellationToken = default) where T : RecordBase
        => _files.GetAllAsync<T>(cancellationToken);

    public Task<T?> GetAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : RecordBase
        => _files.GetAsync<T>(id, cancellationToken);

    // ---- List reads come from the index --------------------------------------

    public Task<IReadOnlyList<ProjectListItem>> GetProjectsAsync(CancellationToken cancellationToken = default)
        => _index.GetProjectsAsync(cancellationToken);

    public Task<IReadOnlyList<SectionListItem>> GetSectionsByProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
        => _index.GetSectionsByProjectAsync(projectId, cancellationToken);

    public Task<IReadOnlyList<LabelListItem>> GetLabelsAsync(CancellationToken cancellationToken = default)
        => _index.GetLabelsAsync(cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetInboxAsync(CancellationToken cancellationToken = default)
        => _index.GetInboxAsync(cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetByProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
        => _index.GetByProjectAsync(projectId, cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetBySectionAsync(Guid sectionId, CancellationToken cancellationToken = default)
        => _index.GetBySectionAsync(sectionId, cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetByLabelAsync(Guid labelId, CancellationToken cancellationToken = default)
        => _index.GetByLabelAsync(labelId, cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetSubtasksAsync(Guid parentTaskId, CancellationToken cancellationToken = default)
        => _index.GetSubtasksAsync(parentTaskId, cancellationToken);

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
