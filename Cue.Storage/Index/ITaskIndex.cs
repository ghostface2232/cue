using Cue.Domain;

namespace Cue.Storage.Index;

/// <summary>
/// The read side of the app: a lightweight, rebuildable index over the task files that answers every
/// list query without scanning the folder. It is a <i>derived</i> store — deleting its backing file
/// loses nothing, because it is reconstructed from the per-record files on the next startup.
/// </summary>
/// <remarks>
/// Queries fall on two axes:
/// <list type="bullet">
/// <item><b>Classification</b> — by container: a task group, a tag, or the home "모든 할 일" (AllTasks)
/// list which spans every group. These return non-deleted task rows; completed rows are kept visible
/// and dimmed in active lists, while badge counts below stay open-only.</item>
/// <item><b>Time</b> — Today / Upcoming / Anytime / Logbook, all computed from the single When date.
/// These are <i>never stored</i>: each is computed by comparing the task's When date against the
/// current day at query time, which is exactly what lets an item scheduled for a past date roll
/// forward into Today.</item>
/// </list>
/// Tombstones (records with a non-null <see cref="RecordBase.DeletedAt"/>) are excluded from every
/// query here — filtering them out is this layer's job, not the store's.
/// </remarks>
public interface ITaskIndex
{
    // Live navigation records

    /// <summary>Active task groups (pure groups), excluding tombstones.</summary>
    Task<IReadOnlyList<TaskGroupListItem>> GetTaskGroupsAsync(CancellationToken cancellationToken = default);

    /// <summary>Active tags, excluding tombstones.</summary>
    Task<IReadOnlyList<TagListItem>> GetTagsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Open (non-deleted, non-completed) task count per group, keyed by group id. Groups with
    /// no open tasks are simply absent from the map. Drives the navigation count badges.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> GetOpenTaskCountsByTaskGroupAsync(CancellationToken cancellationToken = default);

    /// <summary>Open (non-deleted, non-completed) task count per tag, keyed by tag id.</summary>
    Task<IReadOnlyDictionary<Guid, int>> GetOpenTaskCountsByTagAsync(CancellationToken cancellationToken = default);

    /// <summary>Open (non-deleted, non-completed) task count carrying no group — the 그룹 없음 badge.</summary>
    Task<int> GetOpenTaskCountWithoutTaskGroupAsync(CancellationToken cancellationToken = default);

    /// <summary>Open (non-deleted, non-completed) task count carrying no tag — the 태그 없음 badge.</summary>
    Task<int> GetOpenTaskCountWithoutTagAsync(CancellationToken cancellationToken = default);

    // Classification axis

    /// <summary>Every non-deleted task regardless of group — the home "모든 할 일" (AllTasks) list. Each
    /// row carries its embedded checklist for the nested rows the view shows under it.</summary>
    Task<IReadOnlyList<TaskListItem>> GetAllActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Non-deleted tasks belonging to the given task group.</summary>
    Task<IReadOnlyList<TaskListItem>> GetByTaskGroupAsync(Guid taskGroupId, CancellationToken cancellationToken = default);

    /// <summary>Non-deleted tasks carrying the given tag.</summary>
    Task<IReadOnlyList<TaskListItem>> GetByTagAsync(Guid tagId, CancellationToken cancellationToken = default);

    /// <summary>Non-deleted tasks in no group at all — the 그룹 없음 list that re-gathers unfiled
    /// captures.</summary>
    Task<IReadOnlyList<TaskListItem>> GetWithoutTaskGroupAsync(CancellationToken cancellationToken = default);

    /// <summary>Non-deleted tasks carrying no tag at all — the 태그 없음 list that re-gathers unfiled
    /// captures.</summary>
    Task<IReadOnlyList<TaskListItem>> GetWithoutTagAsync(CancellationToken cancellationToken = default);

    // Time axis (computed against the current day)

    /// <summary>
    /// Non-deleted tasks actionable today: a When date on today <i>or earlier</i>. Past dates roll
    /// forward into Today rather than being missed; completed rows remain visible and dimmed.
    /// </summary>
    Task<IReadOnlyList<TaskListItem>> GetTodayAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Non-deleted tasks with a When date on a future day. Excludes anything already due (those are
    /// in Today); completed rows remain visible and dimmed.
    /// </summary>
    Task<IReadOnlyList<TaskListItem>> GetUpcomingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Non-deleted tasks with no When date (Unscheduled) — the "언젠가" (Anytime) bucket. Completed
    /// rows remain visible and dimmed.
    /// </summary>
    Task<IReadOnlyList<TaskListItem>> GetAnytimeAsync(CancellationToken cancellationToken = default);

    /// <summary>Completed tasks, most-recently-completed first — the Logbook.</summary>
    Task<IReadOnlyList<TaskListItem>> GetLogbookAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Tasks that carry a priority flag (P1–P4), for the 중요도 view. Ordered by priority (P1 first),
    /// then completed-last, then rank — the view model groups them into per-priority sections.
    /// Includes completed tasks (shown dimmed) so finished work stays visible until navigation.
    /// </summary>
    Task<IReadOnlyList<TaskListItem>> GetByPriorityAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Tasks whose single When date falls within the inclusive date range. This is the read model
    /// for the timeline view (one point per task); it remains fully rebuildable from the per-record
    /// files. Only tasks with a concrete When (OnDate) appear.
    /// </summary>
    Task<IReadOnlyList<TimelineTaskItem>> GetTimelineAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken = default);
}
