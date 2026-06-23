namespace Cue.Domain;

/// <summary>
/// A task group — a pure top-level container that groups <see cref="TaskItem"/>s. It carries no date
/// and no completion state of its own; an unused group is simply soft-deleted (tombstoned via
/// <see cref="RecordBase.DeletedAt"/>).
/// </summary>
public sealed class TaskGroup : RecordBase, ISortable
{
    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Free-form notes/description, stored as Markdown. <c>null</c> when empty.</summary>
    public string? Notes { get; set; }

    /// <summary>Accent color as a hex string (e.g. "#4F8CC9"). <c>null</c> uses the default.</summary>
    public string? Color { get; set; }

    /// <summary>Sidebar icon as a Segoe Fluent glyph string (e.g. ""). <c>null</c> uses the default.</summary>
    public string? Icon { get; set; }

    /// <summary>How the group is displayed (list vs. board).</summary>
    public TaskGroupView View { get; set; } = TaskGroupView.List;

    /// <summary>
    /// Manual ordering rank among the top-level groups — a LexoRank-style fractional string (see
    /// <see cref="TaskItem.SortOrder"/>). Assigned by the store; the domain only holds it.
    /// </summary>
    public string SortOrder { get; set; } = string.Empty;
}
