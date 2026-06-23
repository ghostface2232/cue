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
        var projects = await _files.GetAllAsync<Project>(cancellationToken).ConfigureAwait(false);
        var labels = await _files.GetAllAsync<Label>(cancellationToken).ConfigureAwait(false);
        await _index.RebuildAsync(tasks, projects, labels, cancellationToken).ConfigureAwait(false);
    }

    // ---- Write path: file first, then index (always both) --------------------

    public async Task SaveAsync<T>(T record, CancellationToken cancellationToken = default) where T : RecordBase
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await SaveCoreAsync(record, cancellationToken).ConfigureAwait(false); }
        finally { _mutationGate.Release(); }
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
        if (task.ProjectId is { } projectId)
        {
            var project = await _files.GetAsync<Project>(projectId, cancellationToken).ConfigureAwait(false);
            if (project?.IsDeleted == true)
                task.ProjectId = null;
        }

        if (task.LabelIds.Count > 0)
        {
            var retainedLabels = new List<Guid>(task.LabelIds.Count);
            foreach (var labelId in task.LabelIds.Distinct())
            {
                var label = await _files.GetAsync<Label>(labelId, cancellationToken).ConfigureAwait(false);
                if (label?.IsDeleted != true) retainedLabels.Add(labelId);
            }
            task.LabelIds = retainedLabels;
        }
    }

    public async Task DeleteAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : RecordBase
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (typeof(T) == typeof(Project)) await RunContainerDeletionCoreAsync(ContainerDeletionKind.Project, id, cascadeTasks: false, cancellationToken).ConfigureAwait(false);
            else if (typeof(T) == typeof(Label)) await RunContainerDeletionCoreAsync(ContainerDeletionKind.Label, id, cascadeTasks: false, cancellationToken).ConfigureAwait(false);
            else if (typeof(T) == typeof(TaskItem)) await DeleteTaskSubtreeAsync(id, cancellationToken).ConfigureAwait(false);
            else await SoftDeleteAndReflectAsync<T>(id, cancellationToken).ConfigureAwait(false);
        }
        finally { _mutationGate.Release(); }
    }

    /// <summary>
    /// Deletes a group with an explicit task disposition. <see cref="ProjectDeletionMode.Reparent"/>
    /// matches the generic <see cref="DeleteAsync{T}"/> default (move tasks to the Cue home);
    /// <see cref="ProjectDeletionMode.DeleteTasks"/> soft-deletes the group's tasks alongside it. Both
    /// run through the durable deletion journal.
    /// </summary>
    public async Task DeleteProjectAsync(Guid projectId, ProjectDeletionMode mode, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await RunContainerDeletionCoreAsync(ContainerDeletionKind.Project, projectId, mode == ProjectDeletionMode.DeleteTasks, cancellationToken).ConfigureAwait(false); }
        finally { _mutationGate.Release(); }
    }

    /// <summary>
    /// Soft-deletes a task and its whole subtask subtree (depth-first), so a deleted parent never
    /// leaves orphaned, unreachable children behind. Each node is tombstoned and reflected into the
    /// index individually.
    /// </summary>
    private async Task DeleteTaskSubtreeAsync(Guid id, CancellationToken cancellationToken)
    {
        foreach (var child in await _index.GetSubtasksAsync(id, cancellationToken).ConfigureAwait(false))
            await DeleteTaskSubtreeAsync(child.Id, cancellationToken).ConfigureAwait(false);
        await SoftDeleteAndReflectAsync<TaskItem>(id, cancellationToken).ConfigureAwait(false);
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
            ContainerDeletionKind.Project when operation.CascadeTasks => DeleteProjectCascadingTasksAsync(operation.TargetId, cancellationToken),
            ContainerDeletionKind.Project => ReparentProjectTasksAndDeleteAsync(operation.TargetId, cancellationToken),
            ContainerDeletionKind.Label => DeleteLabelAsync(operation.TargetId, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation.Kind)),
        };

    private async Task ReflectAsync<T>(T record, CancellationToken cancellationToken) where T : RecordBase
    {
        switch (record)
        {
            case TaskItem task: await _index.ReflectAsync(task, cancellationToken).ConfigureAwait(false); break;
            case Project project: await _index.ReflectAsync(project, cancellationToken).ConfigureAwait(false); break;
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

    private async Task ReparentProjectTasksAndDeleteAsync(Guid projectId, CancellationToken cancellationToken)
    {
        // Least-destructive default ("그룹만 제거"): preserve user work by ungrouping every live task
        // (clear ProjectId) rather than cascading task tombstones. The task stays visible in the home
        // "모든 할 일" (All) list. With sections gone the reparent is a single step.
        foreach (var taskId in await _index.GetTaskIdsByProjectAsync(projectId, cancellationToken).ConfigureAwait(false))
        {
            var task = await _files.GetAsync<TaskItem>(taskId, cancellationToken).ConfigureAwait(false);
            if (task is null || task.ProjectId != projectId) continue;
            task.ProjectId = null;
            await SaveCoreAsync(task, cancellationToken).ConfigureAwait(false);
        }

        await SoftDeleteAndReflectAsync<Project>(projectId, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteProjectCascadingTasksAsync(Guid projectId, CancellationToken cancellationToken)
    {
        // Opt-in destructive deletion: tombstone every task filed under the group (open and completed,
        // including their subtask subtrees, which share the project) before the group itself. Idempotent
        // — already-tombstoned tasks are excluded by the index query, so a resumed crash re-runs cleanly.
        foreach (var taskId in await _index.GetTaskIdsByProjectAsync(projectId, cancellationToken).ConfigureAwait(false))
            await SoftDeleteAndReflectAsync<TaskItem>(taskId, cancellationToken).ConfigureAwait(false);

        await SoftDeleteAndReflectAsync<Project>(projectId, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteLabelAsync(Guid labelId, CancellationToken cancellationToken)
    {
        // Labels are cross-cutting metadata. Delete only the label record and remove its references;
        // never delete or relocate a task merely because one of its labels was removed.
        foreach (var taskId in await _index.GetTaskIdsByLabelAsync(labelId, cancellationToken).ConfigureAwait(false))
        {
            var task = await _files.GetAsync<TaskItem>(taskId, cancellationToken).ConfigureAwait(false);
            if (task is null || task.LabelIds.RemoveAll(id => id == labelId) == 0) continue;
            await SaveCoreAsync(task, cancellationToken).ConfigureAwait(false);
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

    public Task<IReadOnlyList<LabelListItem>> GetLabelsAsync(CancellationToken cancellationToken = default)
        => _index.GetLabelsAsync(cancellationToken);

    public Task<IReadOnlyDictionary<Guid, int>> GetOpenTaskCountsByProjectAsync(CancellationToken cancellationToken = default)
        => _index.GetOpenTaskCountsByProjectAsync(cancellationToken);

    public Task<IReadOnlyDictionary<Guid, int>> GetOpenTaskCountsByLabelAsync(CancellationToken cancellationToken = default)
        => _index.GetOpenTaskCountsByLabelAsync(cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetAllActiveAsync(CancellationToken cancellationToken = default)
        => _index.GetAllActiveAsync(cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetByProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
        => _index.GetByProjectAsync(projectId, cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetByLabelAsync(Guid labelId, CancellationToken cancellationToken = default)
        => _index.GetByLabelAsync(labelId, cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetSubtasksAsync(Guid parentTaskId, CancellationToken cancellationToken = default)
        => _index.GetSubtasksAsync(parentTaskId, cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetTodayAsync(CancellationToken cancellationToken = default)
        => _index.GetTodayAsync(cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetUpcomingAsync(CancellationToken cancellationToken = default)
        => _index.GetUpcomingAsync(cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetAnytimeAsync(CancellationToken cancellationToken = default)
        => _index.GetAnytimeAsync(cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetLogbookAsync(CancellationToken cancellationToken = default)
        => _index.GetLogbookAsync(cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> GetByPriorityAsync(CancellationToken cancellationToken = default)
        => _index.GetByPriorityAsync(cancellationToken);

    public Task<IReadOnlyList<TimelineTaskItem>> GetTimelineAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken = default)
        => _index.GetTimelineAsync(start, end, cancellationToken);

    public ValueTask DisposeAsync() => _index.DisposeAsync();
}
