using Cue.Domain;

namespace Cue.Storage;

/// <summary>What happens to a group's tasks when the group itself is deleted.</summary>
public enum ProjectDeletionMode
{
    /// <summary>Move the group's tasks to the unclassified Cue home (clear their <see cref="TaskItem.ProjectId"/>).
    /// The least-destructive default — the same behavior as <see cref="ITaskStore.DeleteAsync{T}"/> on a <see cref="Project"/>.</summary>
    Reparent,

    /// <summary>Soft-delete every task in the group along with the group.</summary>
    DeleteTasks,
}

/// <summary>
/// Container-deletion policies that go beyond the generic <see cref="ITaskStore.DeleteAsync{T}"/> —
/// specifically deleting a <see cref="Project"/> with an explicit disposition for its tasks. Kept off
/// <see cref="ITaskStore"/> so the plain file store stays free of the crash-safe deletion saga, which
/// lives in the indexed store.
/// </summary>
public interface IContainerDeletionStore
{
    /// <summary>
    /// Deletes a group, disposing of its tasks per <paramref name="mode"/>. Both modes run through the
    /// durable deletion journal so a crash mid-operation is resumed on the next startup.
    /// </summary>
    Task DeleteProjectAsync(Guid projectId, ProjectDeletionMode mode, CancellationToken cancellationToken = default);
}
