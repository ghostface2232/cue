using Cue.Domain;

namespace Cue.Storage;

/// <summary>What happens to a group's tasks when the group itself is deleted.</summary>
public enum TaskGroupDeletionMode
{
    /// <summary>Move the group's tasks to the unclassified Cue home (clear their <see cref="TaskItem.TaskGroupId"/>).
    /// The least-destructive default — the same behavior as <see cref="ITaskStore.DeleteAsync{T}"/> on a <see cref="TaskGroup"/>.</summary>
    Reparent,

    /// <summary>Soft-delete every task in the group along with the group.</summary>
    DeleteTasks,
}

/// <summary>
/// Container-deletion policies that go beyond the generic <see cref="ITaskStore.DeleteAsync{T}"/> —
/// specifically deleting a <see cref="TaskGroup"/> with an explicit disposition for its tasks. Kept off
/// <see cref="ITaskStore"/> so the plain file store stays free of the crash-safe deletion saga, which
/// lives in the indexed store.
/// </summary>
public interface IContainerDeletionStore
{
    /// <summary>
    /// Deletes a group, disposing of its tasks per <paramref name="mode"/>. Both modes run through the
    /// durable deletion journal so a crash mid-operation is resumed on the next startup.
    /// </summary>
    Task DeleteTaskGroupAsync(Guid taskGroupId, TaskGroupDeletionMode mode, CancellationToken cancellationToken = default);
}
