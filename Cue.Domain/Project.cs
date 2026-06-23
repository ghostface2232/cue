namespace Cue.Domain;

/// <summary>
/// A project — a top-level container that groups <see cref="TaskItem"/>s and, optionally,
/// <see cref="Section"/>s.
/// </summary>
public sealed class Project : RecordBase, ISortable
{
    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Free-form notes/description, stored as Markdown. <c>null</c> when empty.</summary>
    public string? Notes { get; set; }

    /// <summary>Accent color as a hex string (e.g. "#4F8CC9"). <c>null</c> uses the default.</summary>
    public string? Color { get; set; }

    /// <summary>Sidebar icon as a Segoe Fluent glyph string (e.g. ""). <c>null</c> uses the default.</summary>
    public string? Icon { get; set; }

    /// <summary>Optional deadline for the project as a whole. Stores UTC plus the original zone.</summary>
    public ZonedDateTime? Deadline { get; set; }

    /// <summary>Whether the project is archived (hidden from active lists but not deleted).</summary>
    public bool IsArchived { get; set; }

    private DateTimeOffset? _completedAt;

    /// <summary>
    /// When the project was marked complete (UTC instant). <c>null</c> while active. Completion is
    /// represented solely by this field, mirroring <see cref="TaskItem.CompletedAt"/> — including
    /// the setter's UTC normalization, so a caller-supplied offset is flattened to a UTC instant
    /// (invariant 7).
    /// </summary>
    public DateTimeOffset? CompletedAt
    {
        get => _completedAt;
        set => _completedAt = value?.ToUniversalTime();
    }

    /// <summary>Whether the project is complete — derived from <see cref="CompletedAt"/> being set.</summary>
    public bool IsCompleted => CompletedAt is not null;

    /// <summary>How the project is displayed (list vs. board).</summary>
    public ProjectView View { get; set; } = ProjectView.List;

    /// <summary>
    /// Manual ordering rank among the top-level projects — a LexoRank-style fractional string (see
    /// <see cref="TaskItem.SortOrder"/>). Assigned by the store; the domain only holds it.
    /// </summary>
    public string SortOrder { get; set; } = string.Empty;
}
