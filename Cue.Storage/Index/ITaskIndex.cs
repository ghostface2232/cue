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
/// list which spans every group. Active lists return non-deleted, <i>open</i> task rows only —
/// completed tasks are excluded from the live lists and surfaced separately (a Today / group / tag
/// "completed" section, or the Logbook). Badge counts stay open-only.</item>
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

    /// <summary>Every non-deleted, <i>open</i> task regardless of group — the home "모든 할 일" (AllTasks)
    /// list. Completed tasks are excluded entirely. Each row carries its embedded checklist for the
    /// nested rows the view shows under it.</summary>
    Task<IReadOnlyList<TaskListItem>> GetAllActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Non-deleted, open tasks belonging to the given task group. Completed tasks are excluded —
    /// they surface in the group's collapsible "완료한 일" section via
    /// <see cref="GetCompletedByTaskGroupAsync"/>.</summary>
    Task<IReadOnlyList<TaskListItem>> GetByTaskGroupAsync(Guid taskGroupId, CancellationToken cancellationToken = default);

    /// <summary>Non-deleted, open tasks carrying the given tag. Completed tasks are excluded — they
    /// surface in the tag's collapsible "완료한 일" section via <see cref="GetCompletedByTagAsync"/>.</summary>
    Task<IReadOnlyList<TaskListItem>> GetByTagAsync(Guid tagId, CancellationToken cancellationToken = default);

    /// <summary>Completed tasks belonging to the given task group, most-recently-completed first — a page
    /// of the rows of the group's collapsible "완료한 일" section. The section pages its rows in (it starts
    /// showing only its count), so callers ask for a <paramref name="limit"/> window from
    /// <paramref name="offset"/>; the defaults return the whole list.</summary>
    Task<IReadOnlyList<TaskListItem>> GetCompletedByTaskGroupAsync(Guid taskGroupId, int limit = int.MaxValue, int offset = 0, CancellationToken cancellationToken = default);

    /// <summary>Completed tasks carrying the given tag, most-recently-completed first — a page of the rows
    /// of the tag's collapsible "완료한 일" section. Paged like
    /// <see cref="GetCompletedByTaskGroupAsync"/>.</summary>
    Task<IReadOnlyList<TaskListItem>> GetCompletedByTagAsync(Guid tagId, int limit = int.MaxValue, int offset = 0, CancellationToken cancellationToken = default);

    /// <summary>Count of completed tasks belonging to the given task group — the header number for the
    /// group's collapsible "완료한 일" section, so it can show its total without realizing a single row.</summary>
    Task<int> GetCompletedCountByTaskGroupAsync(Guid taskGroupId, CancellationToken cancellationToken = default);

    /// <summary>Count of completed tasks carrying the given tag — the header number for the tag's
    /// collapsible "완료한 일" section.</summary>
    Task<int> GetCompletedCountByTagAsync(Guid tagId, CancellationToken cancellationToken = default);

    /// <summary>Non-deleted, open tasks in no group at all — the 그룹 없음 list that re-gathers unfiled
    /// captures. Completed tasks are excluded.</summary>
    Task<IReadOnlyList<TaskListItem>> GetWithoutTaskGroupAsync(CancellationToken cancellationToken = default);

    /// <summary>Non-deleted, open tasks carrying no tag at all — the 태그 없음 list that re-gathers unfiled
    /// captures. Completed tasks are excluded.</summary>
    Task<IReadOnlyList<TaskListItem>> GetWithoutTagAsync(CancellationToken cancellationToken = default);

    // Time axis (computed against the current day)

    /// <summary>
    /// Non-deleted, open tasks actionable today: a When date on today <i>or earlier</i>. Past dates roll
    /// forward into Today rather than being missed. Completed tasks are excluded — those completed today
    /// surface in the Today view's collapsible "오늘 완료한 일" section via
    /// <see cref="GetTodayCompletedAsync"/>.
    /// </summary>
    Task<IReadOnlyList<TaskListItem>> GetTodayAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Tasks completed today (their <see cref="RecordBase"/> completion instant falls on the current
    /// local day), most-recently-completed first — a page of the rows of the Today view's collapsible
    /// "오늘 완료한 일" section. A repeating task's completed copy lands here on the day it was finished. The
    /// section pages its rows in, so callers ask for a <paramref name="limit"/> window from
    /// <paramref name="offset"/>; the defaults return the whole list.
    /// </summary>
    Task<IReadOnlyList<TaskListItem>> GetTodayCompletedAsync(int limit = int.MaxValue, int offset = 0, CancellationToken cancellationToken = default);

    /// <summary>Count of tasks completed today — the header number for the Today view's collapsible
    /// "오늘 완료한 일" section, so it can show its total without realizing a single row.</summary>
    Task<int> GetTodayCompletedCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Non-deleted, open tasks with a When date on a future day. Excludes anything already due (those are
    /// in Today); completed tasks are excluded entirely.
    /// </summary>
    Task<IReadOnlyList<TaskListItem>> GetUpcomingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Non-deleted, open tasks with no When date (Unscheduled) — the "언젠가" (Anytime) bucket. Completed
    /// tasks are excluded entirely.
    /// </summary>
    Task<IReadOnlyList<TaskListItem>> GetAnytimeAsync(CancellationToken cancellationToken = default);

    /// <summary>Completed tasks, most-recently-completed first — the Logbook. Carries each row's
    /// completion instant so the view can group them by day (오늘 / 어제 / a date).</summary>
    Task<IReadOnlyList<TaskListItem>> GetLogbookAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Every active, <i>open</i> task, for the 중요도 view. Ordered by priority (P1 first) with
    /// unprioritized tasks last, then rank — the view model groups them into per-priority sections plus a
    /// trailing 없음 (no priority) bucket. Completed tasks are excluded entirely.
    /// </summary>
    Task<IReadOnlyList<TaskListItem>> GetByPriorityAsync(CancellationToken cancellationToken = default);

    // Recurrence history (the detail-panel timeline)

    /// <summary>
    /// A page of a recurring series' recorded past cycles, most-recent first — the rows behind the
    /// detail-panel timeline. The timeline shows a recent window and pages older cycles in on demand, so
    /// it never eager-loads a long history: callers ask for a <paramref name="limit"/> window from
    /// <paramref name="offset"/>; the defaults return the whole history. Tombstoned occurrences are
    /// excluded. The matching <see cref="GetOccurrenceCountAsync"/> answers the total without realizing rows.
    /// </summary>
    Task<IReadOnlyList<OccurrenceListItem>> GetOccurrencesAsync(Guid seriesId, int limit = int.MaxValue, int offset = 0, CancellationToken cancellationToken = default);

    /// <summary>Count of a series' recorded past cycles (excluding tombstones) — lets the timeline know
    /// whether there are older cycles to page in without realizing them.</summary>
    Task<int> GetOccurrenceCountAsync(Guid seriesId, CancellationToken cancellationToken = default);
}
