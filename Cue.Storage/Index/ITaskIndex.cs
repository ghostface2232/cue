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
/// <item><b>Classification</b> — by container: a project, a label, or the unclassified Cue home
/// (tasks with no project). These return the actionable (open, non-deleted) tasks in that
/// container.</item>
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
    // ---- Live navigation records --------------------------------------------

    /// <summary>Active projects (pure groups), excluding tombstones.</summary>
    Task<IReadOnlyList<ProjectListItem>> GetProjectsAsync(CancellationToken cancellationToken = default);

    /// <summary>Active labels, excluding tombstones.</summary>
    Task<IReadOnlyList<LabelListItem>> GetLabelsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Open (non-deleted, non-completed) task count per project, keyed by project id. Projects with
    /// no open tasks are simply absent from the map. Drives the navigation count badges.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> GetOpenTaskCountsByProjectAsync(CancellationToken cancellationToken = default);

    /// <summary>Open (non-deleted, non-completed) task count per label, keyed by label id.</summary>
    Task<IReadOnlyDictionary<Guid, int>> GetOpenTaskCountsByLabelAsync(CancellationToken cancellationToken = default);

    // ---- Classification axis -------------------------------------------------

    /// <summary>Open tasks with no owning project — the unclassified Cue home list.</summary>
    Task<IReadOnlyList<TaskListItem>> GetInboxAsync(CancellationToken cancellationToken = default);

    /// <summary>Open tasks belonging to the given project.</summary>
    Task<IReadOnlyList<TaskListItem>> GetByProjectAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>Open tasks carrying the given label.</summary>
    Task<IReadOnlyList<TaskListItem>> GetByLabelAsync(Guid labelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Full task records projected as rows whose <see cref="TaskItem.ParentTaskId"/> matches the
    /// parent. Includes completed children so a parent detail can show and reopen them; excludes
    /// tombstones.
    /// </summary>
    Task<IReadOnlyList<TaskListItem>> GetSubtasksAsync(Guid parentTaskId, CancellationToken cancellationToken = default);

    // ---- Time axis (computed against the current day) ------------------------

    /// <summary>
    /// Open tasks that are actionable today: a When date on today <i>or earlier</i>. Past dates roll
    /// forward into Today rather than being missed.
    /// </summary>
    Task<IReadOnlyList<TaskListItem>> GetTodayAsync(CancellationToken cancellationToken = default);

    /// <summary>Open tasks with a When date on a future day. Excludes anything already due (those are in Today).</summary>
    Task<IReadOnlyList<TaskListItem>> GetUpcomingAsync(CancellationToken cancellationToken = default);

    /// <summary>Open tasks with no When date (Unscheduled) — the "언젠가" (Anytime) bucket.</summary>
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
