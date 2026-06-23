using Cue.Domain;

namespace Cue.Storage.Index;

/// <summary>
/// A flattened, read-only projection of a <see cref="TaskItem"/> as it lives in the query index —
/// just the fields a list needs to render and sort. Returned by every <see cref="ITaskIndex"/>
/// query so callers read lists straight from SQLite without ever touching the per-record files.
/// </summary>
/// <remarks>
/// This is a query DTO, not a domain type: it is derived entirely from the file that is the source
/// of truth, and carries no field that could not be rebuilt from that file. Detail-heavy fields
/// (Notes, Recurrence, full label set, the raw <see cref="ZonedDateTime"/> instants) are deliberately
/// omitted — a detail view loads the full <see cref="TaskItem"/> by id from the store when needed.
/// <para>
/// The When date is stored as a calendar <see cref="DateOnly"/> value — the local date the user
/// pinned, in the task's own time zone — because every time-axis view is a date comparison. The
/// index never stores which view a task falls into; that is computed at query time against the
/// current day.
/// </para>
/// </remarks>
public sealed record TaskListItem(
    Guid Id,
    string Title,
    Guid? TaskGroupId,
    Guid? ParentTaskId,
    WhenKind WhenKind,
    DateOnly? WhenDate,
    TimeOnly? WhenTime,
    bool IsCompleted,
    Priority Priority,
    string SortOrder,
    string? TaskGroupName = null,
    string? TaskGroupIcon = null,
    IReadOnlyList<TaskListTag>? Tags = null);

/// <summary>A task's tag as a list row needs it: the display name and its optional hex color. Derived
/// from the index join, so it carries no id — the row only shows it, never edits by it.</summary>
public sealed record TaskListTag(string Name, string? Color);
