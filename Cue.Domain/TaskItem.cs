namespace Cue.Domain;

/// <summary>
/// A single to-do — the core record of the app.
/// </summary>
/// <remarks>
/// Membership is flexible, mirroring Things: a task can live directly in an <see cref="Area"/>
/// (<see cref="AreaId"/>), inside a <see cref="Project"/> (<see cref="ProjectId"/>, optionally
/// grouped under a <see cref="Section"/>), or in neither — an unclassified Inbox item. All of
/// those id fields are optional.
/// <para>
/// Sub-tasks are <i>not</i> a lightweight checklist: each is its own <see cref="TaskItem"/>
/// record (own file) pointing at its parent via <see cref="ParentTaskId"/>, so a sub-task can
/// carry its own deadline, labels, priority, and recurrence.
/// </para>
/// </remarks>
public sealed class TaskItem : RecordBase
{
    /// <summary>Short title — the text the user types on the quick-add line.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Free-form notes, stored as Markdown. <c>null</c> when empty.</summary>
    public string? Notes { get; set; }

    /// <summary>Whether the task is checked off.</summary>
    public bool IsCompleted { get; set; }

    /// <summary>When the task was completed (UTC). <c>null</c> while incomplete.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// Hard due date — the date something is actually <i>due</i> (Things' "Deadline").
    /// In the default single-date workflow this is the only date used.
    /// </summary>
    public ZonedDateTime? Deadline { get; set; }

    /// <summary>
    /// Optional scheduled / start date — when the task should surface in Today/Upcoming
    /// (Things' "When"), independent of the <see cref="Deadline"/>. Left null unless the user
    /// chooses to split the two.
    /// </summary>
    public ZonedDateTime? When { get; set; }

    /// <summary>Priority flag.</summary>
    public Priority Priority { get; set; } = Priority.None;

    /// <summary>Owning project, if any. <c>null</c> means the task is not in a project.</summary>
    public Guid? ProjectId { get; set; }

    /// <summary>Grouping section/heading within the project, if any.</summary>
    public Guid? SectionId { get; set; }

    /// <summary>Owning area for tasks placed directly in an area (not via a project).</summary>
    public Guid? AreaId { get; set; }

    /// <summary>Parent task when this is a sub-task; <c>null</c> for a top-level task.</summary>
    public Guid? ParentTaskId { get; set; }

    /// <summary>Cross-cutting labels applied to this task.</summary>
    public List<Guid> LabelIds { get; set; } = new();

    /// <summary>Recurrence rule, if the task repeats. <c>null</c> for a one-off task.</summary>
    public RecurrenceRule? Recurrence { get; set; }

    /// <summary>Manual ordering weight within its list. Lower sorts first.</summary>
    public double SortOrder { get; set; }
}
