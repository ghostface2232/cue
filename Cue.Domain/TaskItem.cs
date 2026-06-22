namespace Cue.Domain;

/// <summary>
/// A single to-do — the core record of the app.
/// </summary>
/// <remarks>
/// A task's container is a single optional <see cref="ProjectId"/>: it either belongs to one
/// project (optionally grouped under a <see cref="Section"/>) or, when null, is unclassified
/// (free / Inbox).
/// <para>
/// Sub-tasks are <i>not</i> a lightweight checklist: each is its own <see cref="TaskItem"/>
/// record (own file) pointing at its parent via <see cref="ParentTaskId"/>, so a sub-task can
/// carry its own dates, labels, priority, and recurrence.
/// </para>
/// </remarks>
public sealed class TaskItem : RecordBase
{
    /// <summary>Short title — the text the user types on the quick-add line.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Free-form notes, stored as Markdown. <c>null</c> when empty.</summary>
    public string? Notes { get; set; }

    private DateTimeOffset? _completedAt;

    /// <summary>
    /// When the task was completed (UTC instant). <c>null</c> means it is not done. Completion is
    /// represented solely by this field; there is no separate boolean.
    /// </summary>
    /// <remarks>
    /// Unlike CreatedAt/UpdatedAt (which the store stamps from a UTC clock), this is caller-set, so
    /// the setter flattens any incoming offset to a true UTC instant — the same UTC normalization
    /// <see cref="ZonedDateTime"/> applies to itself. This keeps invariant 7 (system timestamps are
    /// UTC instants) true regardless of who assigns it, on save and on deserialize alike. It is a
    /// canonicalization of the value handed in, not a clock read, so it does not break the
    /// pure-holder rule.
    /// </remarks>
    public DateTimeOffset? CompletedAt
    {
        get => _completedAt;
        set => _completedAt = value?.ToUniversalTime();
    }

    /// <summary>Whether the task is done — derived from <see cref="CompletedAt"/> being set.</summary>
    public bool IsCompleted => CompletedAt is not null;

    /// <summary>
    /// Hard due date — when something is actually <i>due</i> (Things' "Deadline"). In the default
    /// single-date workflow this is the only date used. Stores UTC plus the original time zone.
    /// </summary>
    public ZonedDateTime? Deadline { get; set; }

    /// <summary>
    /// Scheduled date — when the user intends to work on the task, and the basis for
    /// Today/Upcoming (Things' "When"). Defaults to <see cref="WhenKind.Unscheduled"/>.
    /// </summary>
    public ScheduledWhen When { get; set; } = ScheduledWhen.Unscheduled;

    /// <summary>Priority flag.</summary>
    public Priority Priority { get; set; } = Priority.None;

    /// <summary>Owning project, if any. <c>null</c> means the task is unclassified (free).</summary>
    public Guid? ProjectId { get; set; }

    /// <summary>Grouping section/heading within the project, if any.</summary>
    public Guid? SectionId { get; set; }

    /// <summary>Parent task when this is a sub-task; <c>null</c> for a top-level task.</summary>
    public Guid? ParentTaskId { get; set; }

    /// <summary>Cross-cutting labels applied to this task.</summary>
    public List<Guid> LabelIds { get; set; } = new();

    /// <summary>Recurrence rule, if the task repeats. <c>null</c> for a one-off task.</summary>
    public RecurrenceRule? Recurrence { get; set; }

    /// <summary>
    /// Manual ordering rank within its list. A LexoRank-style fractional string so a new rank can
    /// always be generated between two neighbors without renumbering. Assigned by the store/rank
    /// service; the domain only holds it.
    /// </summary>
    public string SortOrder { get; set; } = string.Empty;
}
