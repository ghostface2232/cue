namespace Cue.Domain;

/// <summary>
/// The outcome recorded for a single past cycle of a recurring task — one
/// <see cref="RecurrenceOccurrence"/>. The <i>current/next</i> cycle is never an occurrence record;
/// it is the live <see cref="TaskItem"/> itself, so this enum only describes cycles that are already
/// behind the series.
/// </summary>
public enum OccurrenceStatus
{
    /// <summary>완료 — the cycle was carried out (its checklist state is frozen on the occurrence).</summary>
    Completed,

    /// <summary>미수행 — the cycle was not performed.</summary>
    Missed,
}
