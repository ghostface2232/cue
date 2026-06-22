namespace Cue.Domain;

/// <summary>
/// A project — groups <see cref="TaskItem"/>s and, optionally, <see cref="Section"/>s.
/// Sits under an <see cref="Area"/> (or unclassified when <see cref="AreaId"/> is null).
/// </summary>
public sealed class Project : RecordBase
{
    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Free-form notes/description, stored as Markdown. <c>null</c> when empty.</summary>
    public string? Notes { get; set; }

    /// <summary>Owning area. <c>null</c> means the project is unclassified.</summary>
    public Guid? AreaId { get; set; }

    /// <summary>Accent color as a hex string (e.g. "#4F8CC9"). <c>null</c> uses the default.</summary>
    public string? Color { get; set; }

    /// <summary>Optional deadline for the project as a whole.</summary>
    public ZonedDateTime? Deadline { get; set; }

    /// <summary>Whether the project is archived (hidden from active lists but not deleted).</summary>
    public bool IsArchived { get; set; }

    /// <summary>When the project was marked complete (UTC). <c>null</c> while active.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>How the project is displayed (list vs. board).</summary>
    public ProjectView View { get; set; } = ProjectView.List;

    /// <summary>Manual ordering weight within its area. Lower sorts first.</summary>
    public double SortOrder { get; set; }
}
