using Cue.Storage;
using Cue.ViewModels;

namespace Cue.Services;

/// <summary>A validated shell destination for opening one task from outside the page tree.</summary>
internal sealed record TaskOpenRoute(TaskListNavigation Navigation, Guid TaskId);

/// <summary>
/// Resolves an external task id to the list context that owns it. Toast payloads can be stale long after
/// delivery, so missing and tombstoned records return <c>null</c> instead of navigating a page to nowhere.
/// </summary>
internal sealed class TaskOpenRouteResolver(ITaskStore store)
{
    public async Task<TaskOpenRoute?> ResolveAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        if (taskId == Guid.Empty)
            return null;

        var task = await store.GetAsync<Cue.Domain.TaskItem>(taskId, cancellationToken);
        if (task is null || task.IsDeleted)
            return null;

        var mode = task.IsCompleted ? TaskListMode.Logbook : TaskListMode.AllTasks;
        return new TaskOpenRoute(new TaskListNavigation(mode), task.Id);
    }
}
