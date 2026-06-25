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
public sealed class IndexedTaskStore : ITaskStore, ITaskIndex, IContainerDeletionStore, IAsyncDisposable
{
    private readonly ITaskStore _files;
    private readonly SqliteTaskIndex _index;
    private readonly ContainerDeletionJournal? _deletionJournal;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    public IndexedTaskStore(ITaskStore files, SqliteTaskIndex index)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _deletionJournal = files is FileTaskStore fileStore
            ? new ContainerDeletionJournal(fileStore.RootPath)
            : null;
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
        await store.ResumeContainerDeletionsAsync(cancellationToken).ConfigureAwait(false);
        return store;
    }

    /// <summary>
    /// Rebuilds the index from the full set of record files. Run this at startup; it makes the index
    /// match the files exactly, recovering completely even if the database was deleted or is stale.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var tasks = await _files.GetAllAsync<TaskItem>(cancellationToken).ConfigureAwait(false);
        var taskGroups = await _files.GetAllAsync<TaskGroup>(cancellationToken).ConfigureAwait(false);
        var tags = await _files.GetAllAsync<Tag>(cancellationToken).ConfigureAwait(false);
        var occurrences = await _files.GetAllAsync<RecurrenceOccurrence>(cancellationToken).ConfigureAwait(false);
        await _index.RebuildAsync(tasks, taskGroups, tags, occurrences, cancellationToken).ConfigureAwait(false);
    }

    // Write path: file first, then index (always both)

    public async Task SaveAsync<T>(T record, CancellationToken cancellationToken = default) where T : RecordBase
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await SaveCoreAsync(record, cancellationToken).ConfigureAwait(false); }
        finally { _mutationGate.Release(); }
    }

    /// <summary>
    /// Atomic read-modify-write: holds <see cref="_mutationGate"/> across the file read, the caller's
    /// mutation, and the write+index reflect — the same lock <see cref="SaveAsync"/> takes. Several
    /// callers updating different fields of one task (detail metadata, the embedded checklist, a list
    /// completion/checklist toggle) therefore each see the latest persisted record and write back only
    /// their own change, so none can resurrect another's stale copy.
    /// </summary>
    public async Task<T?> MutateAsync<T>(Guid id, Func<T, bool> mutate, CancellationToken cancellationToken = default)
        where T : RecordBase
    {
        ArgumentNullException.ThrowIfNull(mutate);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = await _files.GetAsync<T>(id, cancellationToken).ConfigureAwait(false);
            if (record is null || record.IsDeleted) return null;
            if (!mutate(record)) return null;
            await SaveCoreAsync(record, cancellationToken).ConfigureAwait(false);
            return record;
        }
        finally { _mutationGate.Release(); }
    }

    /// <summary>
    /// Runs <paramref name="work"/> as one atomic unit while holding <see cref="_mutationGate"/>, the
    /// same lock <see cref="SaveAsync"/>/<see cref="MutateAsync"/> take. The scope's reads and saves use
    /// the un-gated core (the gate is already held), so a multi-record operation — recurrence completion
    /// writes the Logbook copy and then advances the original — commits together and cannot interleave
    /// with another save path that would clobber a shared record.
    /// </summary>
    public async Task RunInTransactionAsync(Func<ITaskMutationScope, CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await work(new GatedScope(this), cancellationToken).ConfigureAwait(false); }
        finally { _mutationGate.Release(); }
    }

    /// <summary>The transaction scope handed to <see cref="RunInTransactionAsync"/>: reads come from the
    /// files and saves go through <see cref="SaveCoreAsync"/>, both <i>without</i> re-taking the mutation
    /// gate (the transaction already holds it), so an inner save can't deadlock on the held lock.</summary>
    private sealed class GatedScope(IndexedTaskStore store) : ITaskMutationScope
    {
        public Task<T?> GetAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : RecordBase
            => store._files.GetAsync<T>(id, cancellationToken);

        public Task SaveAsync<T>(T record, CancellationToken cancellationToken = default) where T : RecordBase
            => store.SaveCoreAsync(record, cancellationToken);
    }

    private async Task SaveCoreAsync<T>(T record, CancellationToken cancellationToken) where T : RecordBase
    {
        var existing = await _files.GetAsync<T>(record.Id, cancellationToken).ConfigureAwait(false);
        if (existing?.IsDeleted == true && !record.IsDeleted)
            throw new InvalidOperationException($"A deleted {typeof(T).Name} cannot be restored through SaveAsync.");

        if (record is TaskItem task)
            await NormalizeTaskReferencesAsync(task, cancellationToken).ConfigureAwait(false);

        await _files.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        await ReflectAsync(record, cancellationToken).ConfigureAwait(false);
    }

    private async Task NormalizeTaskReferencesAsync(TaskItem task, CancellationToken cancellationToken)
    {
        if (task.TaskGroupId is { } taskGroupId)
        {
            var taskGroup = await _files.GetAsync<TaskGroup>(taskGroupId, cancellationToken).ConfigureAwait(false);
            if (taskGroup?.IsDeleted == true)
                task.TaskGroupId = null;
        }

        if (task.TagIds.Count > 0)
        {
            var retainedTags = new List<Guid>(task.TagIds.Count);
            foreach (var tagId in task.TagIds.Distinct())
            {
                var tag = await _files.GetAsync<Tag>(tagId, cancellationToken).ConfigureAwait(false);
                if (tag?.IsDeleted != true) retainedTags.Add(tagId);
            }
            task.TagIds = retainedTags;
        }
    }

    public async Task DeleteAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : RecordBase
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (typeof(T) == typeof(TaskGroup)) await RunContainerDeletionCoreAsync(ContainerDeletionKind.TaskGroup, id, cascadeTasks: false, cancellationToken).ConfigureAwait(false);
            else if (typeof(T) == typeof(Tag)) await RunContainerDeletionCoreAsync(ContainerDeletionKind.Tag, id, cascadeTasks: false, cancellationToken).ConfigureAwait(false);
            else await SoftDeleteAndReflectAsync<T>(id, cancellationToken).ConfigureAwait(false);
        }
        finally { _mutationGate.Release(); }
    }

    /// <summary>
    /// Deletes a group with an explicit task disposition. <see cref="TaskGroupDeletionMode.Reparent"/>
    /// matches the generic <see cref="DeleteAsync{T}"/> default (move tasks to the Cue home);
    /// <see cref="TaskGroupDeletionMode.DeleteTasks"/> soft-deletes the group's tasks alongside it. Both
    /// run through the durable deletion journal.
    /// </summary>
    public async Task DeleteTaskGroupAsync(Guid taskGroupId, TaskGroupDeletionMode mode, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await RunContainerDeletionCoreAsync(ContainerDeletionKind.TaskGroup, taskGroupId, mode == TaskGroupDeletionMode.DeleteTasks, cancellationToken).ConfigureAwait(false); }
        finally { _mutationGate.Release(); }
    }

    private async Task RunContainerDeletionCoreAsync(ContainerDeletionKind kind, Guid id, bool cascadeTasks, CancellationToken cancellationToken)
    {
        var operation = new ContainerDeletionOperation { Kind = kind, TargetId = id, CascadeTasks = cascadeTasks };
        if (_deletionJournal is not null) await _deletionJournal.WriteAsync(operation, cancellationToken).ConfigureAwait(false);
        await ApplyContainerDeletionAsync(operation, cancellationToken).ConfigureAwait(false);
        operation.IsCompleted = true;
        if (_deletionJournal is not null) await _deletionJournal.WriteAsync(operation, cancellationToken).ConfigureAwait(false);
    }

    private async Task ResumeContainerDeletionsAsync(CancellationToken cancellationToken)
    {
        if (_deletionJournal is null) return;
        foreach (var operation in await _deletionJournal.GetPendingAsync(cancellationToken).ConfigureAwait(false))
        {
            await ApplyContainerDeletionAsync(operation, cancellationToken).ConfigureAwait(false);
            operation.IsCompleted = true;
            await _deletionJournal.WriteAsync(operation, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task ApplyContainerDeletionAsync(ContainerDeletionOperation operation, CancellationToken cancellationToken)
        => operation.Kind switch
        {
            ContainerDeletionKind.TaskGroup when operation.CascadeTasks => DeleteTaskGroupCascadingTasksAsync(operation.TargetId, cancellationToken),
            ContainerDeletionKind.TaskGroup => ReparentTaskGroupTasksAndDeleteAsync(operation.TargetId, cancellationToken),
            ContainerDeletionKind.Tag => DeleteTagAsync(operation.TargetId, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation.Kind)),
        };

    private async Task ReflectAsync<T>(T record, CancellationToken cancellationToken) where T : RecordBase
    {
        switch (record)
        {
            case TaskItem task: await _index.ReflectAsync(task, cancellationToken).ConfigureAwait(false); break;
            case TaskGroup taskGroup: await _index.ReflectAsync(taskGroup, cancellationToken).ConfigureAwait(false); break;
            case Tag tag: await _index.ReflectAsync(tag, cancellationToken).ConfigureAwait(false); break;
            case RecurrenceOccurrence occurrence: await _index.ReflectAsync(occurrence, cancellationToken).ConfigureAwait(false); break;
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

    private async Task ReparentTaskGroupTasksAndDeleteAsync(Guid taskGroupId, CancellationToken cancellationToken)
    {
        // Least-destructive default ("그룹만 제거"): preserve user work by ungrouping every live task
        // (clear TaskGroupId) rather than cascading task tombstones. The task stays visible in the home
        // "모든 할 일" (AllTasks) list. With sections gone the reparent is a single step.
        foreach (var taskId in await _index.GetTaskIdsByTaskGroupAsync(taskGroupId, cancellationToken).ConfigureAwait(false))
        {
            var task = await _files.GetAsync<TaskItem>(taskId, cancellationToken).ConfigureAwait(false);
            if (task is null || task.TaskGroupId != taskGroupId) continue;
            task.TaskGroupId = null;
            await SaveCoreAsync(task, cancellationToken).ConfigureAwait(false);
        }

        await SoftDeleteAndReflectAsync<TaskGroup>(taskGroupId, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteTaskGroupCascadingTasksAsync(Guid taskGroupId, CancellationToken cancellationToken)
    {
        // Opt-in destructive deletion: tombstone every task filed under the group (open and completed)
        // before the group itself. Idempotent — already-tombstoned tasks are excluded by the index
        // query, so a resumed crash re-runs cleanly.
        foreach (var taskId in await _index.GetTaskIdsByTaskGroupAsync(taskGroupId, cancellationToken).ConfigureAwait(false))
            await SoftDeleteAndReflectAsync<TaskItem>(taskId, cancellationToken).ConfigureAwait(false);

        await SoftDeleteAndReflectAsync<TaskGroup>(taskGroupId, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteTagAsync(Guid tagId, CancellationToken cancellationToken)
    {
        // Tags are cross-cutting metadata. Delete only the tag record and remove its references;
        // never delete or relocate a task merely because one of its tags was removed.
        foreach (var taskId in await _index.GetTaskIdsByTagAsync(tagId, cancellationToken).ConfigureAwait(false))
        {
            var task = await _files.GetAsync<TaskItem>(taskId, cancellationToken).ConfigureAwait(false);
            if (task is null || task.TagIds.RemoveAll(id => id == tagId) == 0) continue;
            await SaveCoreAsync(task, cancellationToken).ConfigureAwait(false);
        }

        await SoftDeleteAndReflectAsync<Tag>(tagId, cancellationToken).ConfigureAwait(false);
    }

    // By-id / full reads come from the files (source of truth)

    public Task<IReadOnlyList<T>> GetAllAsync<T>(CancellationToken cancellationToken = default) where T : RecordBase
        => _files.GetAllAsync<T>(cancellationToken);

    public Task<T?> GetAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : RecordBase
        => _files.GetAsync<T>(id, cancellationToken);

    // List reads come from the index

    public Task<IReadOnlyList<TaskGroupListItem>> GetTaskGroupsAsync(CancellationToken cancellationToken = default)
        => _index.GetTaskGroupsAsync(cancellationToken);

    public Task<IReadOnlyList<TagListItem>> GetTagsAsync(CancellationToken cancellationToken = default)
        => _index.GetTagsAsync(cancellationToken);

    public Task<IReadOnlyDictionary<Guid, int>> GetOpenTaskCountsByTaskGroupAsync(CancellationToken cancellationToken = default)
        => _index.GetOpenTaskCountsByTaskGroupAsync(cancellationToken);

    public Task<IReadOnlyDictionary<Guid, int>> GetOpenTaskCountsByTagAsync(CancellationToken cancellationToken = default)
        => _index.GetOpenTaskCountsByTagAsync(cancellationToken);

    public Task<int> GetOpenTaskCountWithoutTaskGroupAsync(CancellationToken cancellationToken = default)
        => _index.GetOpenTaskCountWithoutTaskGroupAsync(cancellationToken);

    public Task<int> GetOpenTaskCountWithoutTagAsync(CancellationToken cancellationToken = default)
        => _index.GetOpenTaskCountWithoutTagAsync(cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetAllActiveAsync(CancellationToken cancellationToken = default)
        => _index.GetAllActiveAsync(cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetByTaskGroupAsync(Guid taskGroupId, CancellationToken cancellationToken = default)
        => _index.GetByTaskGroupAsync(taskGroupId, cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetByTagAsync(Guid tagId, CancellationToken cancellationToken = default)
        => _index.GetByTagAsync(tagId, cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetCompletedByTaskGroupAsync(Guid taskGroupId, int limit = int.MaxValue, int offset = 0, CancellationToken cancellationToken = default)
        => _index.GetCompletedByTaskGroupAsync(taskGroupId, limit, offset, cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetCompletedByTagAsync(Guid tagId, int limit = int.MaxValue, int offset = 0, CancellationToken cancellationToken = default)
        => _index.GetCompletedByTagAsync(tagId, limit, offset, cancellationToken);

    public Task<int> GetCompletedCountByTaskGroupAsync(Guid taskGroupId, CancellationToken cancellationToken = default)
        => _index.GetCompletedCountByTaskGroupAsync(taskGroupId, cancellationToken);

    public Task<int> GetCompletedCountByTagAsync(Guid tagId, CancellationToken cancellationToken = default)
        => _index.GetCompletedCountByTagAsync(tagId, cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetWithoutTaskGroupAsync(CancellationToken cancellationToken = default)
        => _index.GetWithoutTaskGroupAsync(cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetWithoutTagAsync(CancellationToken cancellationToken = default)
        => _index.GetWithoutTagAsync(cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetTodayAsync(CancellationToken cancellationToken = default)
        => _index.GetTodayAsync(cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetTodayCompletedAsync(int limit = int.MaxValue, int offset = 0, CancellationToken cancellationToken = default)
        => _index.GetTodayCompletedAsync(limit, offset, cancellationToken);

    public Task<int> GetTodayCompletedCountAsync(CancellationToken cancellationToken = default)
        => _index.GetTodayCompletedCountAsync(cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetUpcomingAsync(CancellationToken cancellationToken = default)
        => _index.GetUpcomingAsync(cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetAnytimeAsync(CancellationToken cancellationToken = default)
        => _index.GetAnytimeAsync(cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetLogbookAsync(CancellationToken cancellationToken = default)
        => _index.GetLogbookAsync(cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetByPriorityAsync(CancellationToken cancellationToken = default)
        => _index.GetByPriorityAsync(cancellationToken);

    public Task<IReadOnlyList<OccurrenceListItem>> GetOccurrencesAsync(Guid seriesId, int limit = int.MaxValue, int offset = 0, CancellationToken cancellationToken = default)
        => _index.GetOccurrencesAsync(seriesId, limit, offset, cancellationToken);

    public Task<int> GetOccurrenceCountAsync(Guid seriesId, CancellationToken cancellationToken = default)
        => _index.GetOccurrenceCountAsync(seriesId, cancellationToken);

    public ValueTask DisposeAsync() => _index.DisposeAsync();
}
