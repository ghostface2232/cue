namespace Cue.Domain;

/// <summary>
/// One past cycle of a recurring <see cref="TaskItem"/> — a lightweight history record owned by the
/// series, not a task of its own.
/// </summary>
/// <remarks>
/// This replaces the former "Logbook copy" model, where every completed cycle was a standalone
/// completed <see cref="TaskItem"/>. Those copies were unrelated to one another and to the series, so
/// a series' history was scattered and per-cycle state (skipped / missed, a frozen checklist) could
/// not be expressed. A <see cref="RecurrenceOccurrence"/> instead belongs to one series via
/// <see cref="SeriesId"/>, so the whole history is queryable by series and each cycle carries its own
/// <see cref="Status"/> and <see cref="ChecklistSnapshot"/>.
/// <para>
/// Only <i>past</i> cycles are records. The current/next cycle is the live series
/// <see cref="TaskItem"/> itself (its <see cref="TaskItem.When"/>), so it never has an occurrence row.
/// </para>
/// <para>
/// The id is assigned deterministically by the service from (series id, occurrence instant), so
/// recording the same cycle twice overwrites rather than duplicates — see
/// <c>RecurringTaskService</c>. Editing a past occurrence changes only this record; it never touches
/// the series' <see cref="TaskItem.When"/>, so the next scheduled cycle is unaffected.
/// </para>
/// </remarks>
public sealed class RecurrenceOccurrence : RecordBase
{
    /// <summary>The recurring <see cref="TaskItem"/> this cycle belongs to (its <see cref="RecordBase.Id"/>).</summary>
    public Guid SeriesId { get; set; }

    /// <summary>
    /// The cycle's scheduled date, frozen as the historical record — the series' <see cref="TaskItem.When"/>
    /// at the moment this cycle was recorded, carrying its all-day (종일) flag so the timeline renders the
    /// day exactly as it was scheduled.
    /// </summary>
    public ScheduledWhen When { get; set; } = ScheduledWhen.Unscheduled;

    /// <summary>What became of this cycle — see <see cref="OccurrenceStatus"/>.</summary>
    public OccurrenceStatus Status { get; set; } = OccurrenceStatus.Completed;

    /// <summary>
    /// When the cycle was completed (UTC instant), for a <see cref="OccurrenceStatus.Completed"/> cycle;
    /// <c>null</c> for skipped/missed. Flattened to UTC like <see cref="TaskItem.CompletedAt"/>.
    /// </summary>
    private DateTimeOffset? _completedAt;
    public DateTimeOffset? CompletedAt
    {
        get => _completedAt;
        set => _completedAt = value?.ToUniversalTime();
    }

    /// <summary>
    /// The series' checklist exactly as it was ticked when this cycle was completed — a deep copy so the
    /// record keeps an accurate account of what was done that cycle, independent of the series' later
    /// (reset) checklist. Empty for skipped/missed cycles.
    /// </summary>
    public List<ChecklistItem> ChecklistSnapshot { get; set; } = new();
}
