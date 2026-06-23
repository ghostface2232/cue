using Cue.Domain;

namespace Cue.Storage.Index;

/// <summary>
/// Single-point projection for the timeline view: a task placed on its one When date. The date is
/// the pinned calendar day already derived into the index; callers decide how to position the card
/// on a visible range. Only tasks with a concrete When (OnDate) are projected.
/// </summary>
public sealed record TimelineTaskItem(
    Guid Id,
    string Title,
    DateOnly Date,
    bool IsCompleted,
    Priority Priority);
