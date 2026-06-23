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
/// <item><b>Classification</b> — by container: a project, a section, a label, or the unclassified
/// Inbox (tasks with no project). These return the actionable (open, non-deleted) tasks in that
/// container.</item>
/// <item><b>Time</b> — Today / This Evening / Upcoming / Anytime / Someday / Logbook. These are
/// <i>never stored</i>: each is computed by comparing the task's pinned date against the current day
/// at query time, which is exactly what lets an item scheduled for a past date roll forward into
/// Today.</item>
/// </list>
/// Tombstones (records with a non-null <see cref="RecordBase.DeletedAt"/>) are excluded from every
/// query here — filtering them out is this layer's job, not the store's.
/// </remarks>
public interface ITaskIndex
{
    // ---- Live navigation records --------------------------------------------

    /// <summary>Active projects, excluding tombstones, archived, and completed records.</summary>
    Task<IReadOnlyList<ProjectListItem>> GetProjectsAsync(CancellationToken cancellationToken = default);

    /// <summary>Active sections in a project, excluding tombstones, archived, and completed records.</summary>
    Task<IReadOnlyList<SectionListItem>> GetSectionsByProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

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

    /// <summary>Open tasks with no owning project — the unclassified Inbox / Home list.</summary>
    Task<IReadOnlyList<TaskListItem>> GetInboxAsync(CancellationToken cancellationToken = default);

    /// <summary>Open tasks belonging to the given project.</summary>
    Task<IReadOnlyList<TaskListItem>> GetByProjectAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>Open tasks grouped under the given section.</summary>
    Task<IReadOnlyList<TaskListItem>> GetBySectionAsync(Guid sectionId, CancellationToken cancellationToken = default);

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
    /// Open tasks that are actionable today: a scheduled When on today <i>or earlier</i>, or a
    /// Deadline falling today or earlier (a deadline-only task with no When still surfaces here when
    /// it is due or overdue). Past dates roll forward into Today rather than being missed.
    /// </summary>
    Task<IReadOnlyList<TaskListItem>> GetTodayAsync(CancellationToken cancellationToken = default);

    /// <summary>The subset of Today flagged for the evening.</summary>
    Task<IReadOnlyList<TaskListItem>> GetThisEveningAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Open tasks scheduled for a future day, or carrying a future deadline. Excludes anything
    /// already due (those are in Today).
    /// </summary>
    Task<IReadOnlyList<TaskListItem>> GetUpcomingAsync(CancellationToken cancellationToken = default);

    /// <summary>Open tasks with no scheduled date (Unscheduled) — the "Anytime" bucket.</summary>
    Task<IReadOnlyList<TaskListItem>> GetAnytimeAsync(CancellationToken cancellationToken = default);

    /// <summary>Open tasks parked as Someday (no date).</summary>
    Task<IReadOnlyList<TaskListItem>> GetSomedayAsync(CancellationToken cancellationToken = default);

    /// <summary>Completed tasks, most-recently-completed first — the Logbook.</summary>
    Task<IReadOnlyList<TaskListItem>> GetLogbookAsync(CancellationToken cancellationToken = default);
}
