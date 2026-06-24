namespace Cue.Domain;

/// <summary>
/// A single to-do — the core record of the app.
/// </summary>
/// <remarks>
/// A task's container is a single optional <see cref="TaskGroupId"/>: it either belongs to one
/// group or, when null, has no group. Either way it appears in the home 모든 할 일 (AllTasks) view.
/// <para>
/// A task carries an embedded <see cref="Checklist"/> of lightweight <see cref="ChecklistItem"/>s —
/// each just a title and a checked flag. Checklist items are not records: they
/// have no own file, no dates/tags/priority/recurrence, and cannot nest. They are persisted as part
/// of this task's single JSON file.
/// </para>
/// </remarks>
public sealed class TaskItem : RecordBase, ISortable
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
    /// The task's single date — when the user intends to work on it / when it is due, and the basis
    /// for Today/Upcoming. Two states only: a concrete date (OnDate) or none (Unscheduled).
    /// Defaults to <see cref="WhenKind.Unscheduled"/>.
    /// </summary>
    public ScheduledWhen When { get; set; } = ScheduledWhen.Unscheduled;

    /// <summary>Priority flag.</summary>
    public Priority Priority { get; set; } = Priority.None;

    /// <summary>Owning group, if any. <c>null</c> means the task has no group (still shown in 모든 할 일).</summary>
    public Guid? TaskGroupId { get; set; }

    /// <summary>
    /// Embedded checklist — an ordered list of lightweight items (title + checked). Order is
    /// the list position; there is no nesting and each item is not a record of its own.
    /// </summary>
    public List<ChecklistItem> Checklist { get; set; } = new();

    /// <summary>Cross-cutting tags applied to this task.</summary>
    public List<Guid> TagIds { get; set; } = new();

    /// <summary>Recurrence rule, if the task repeats. <c>null</c> for a one-off task.</summary>
    public RecurrenceRule? Recurrence { get; set; }

    /// <summary>
    /// Manual ordering rank within its list. A LexoRank-style fractional string so a new rank can
    /// always be generated between two neighbors without renumbering. Assigned by the store/rank
    /// service; the domain only holds it.
    /// </summary>
    public string SortOrder { get; set; } = string.Empty;
}
