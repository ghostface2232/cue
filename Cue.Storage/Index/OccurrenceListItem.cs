using Cue.Domain;

namespace Cue.Storage.Index;

/// <summary>
/// A flattened, read-only projection of a <see cref="RecurrenceOccurrence"/> as the detail-panel
/// timeline needs it — just enough to render one pip (its date and status) and to load the full record
/// on demand when the pip is opened.
/// </summary>
/// <remarks>
/// Deliberately omits the checklist snapshot: the timeline shows many pips, and eager-loading every
/// snapshot would defeat the point of paging history in. The per-cycle flyout loads the full
/// <see cref="RecurrenceOccurrence"/> by <see cref="Id"/> from the store only when a pip is opened.
/// </remarks>
public sealed record OccurrenceListItem(
    Guid Id,
    Guid SeriesId,
    DateOnly OccurrenceDate,
    bool IsAllDay,
    OccurrenceStatus Status,
    DateTimeOffset? CompletedAt);
