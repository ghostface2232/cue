using Cue.Domain;

namespace Cue.Storage.Index;

/// <summary>
/// Date-range projection for the timeline view. Dates are the pinned calendar days already derived
/// into the index; callers decide how to lay the item out on a visible range.
/// </summary>
public sealed record TimelineTaskItem(
    Guid Id,
    string Title,
    DateOnly StartDate,
    DateOnly EndDate,
    bool IsCompleted,
    Priority Priority,
    WhenKind WhenKind);
