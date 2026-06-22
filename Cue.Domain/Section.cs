namespace Cue.Domain;

/// <summary>
/// A grouping of tasks inside a project — Todoist's "Section" / Things' "Heading".
/// </summary>
/// <remarks>
/// Modeled as its own record (rather than as an inline heading embedded in the project) on
/// purpose. The whole architecture is one file per record with a stable GUID, and sections
/// need exactly the things a record gives them: an independent identity that survives renames
/// and reordering, soft-delete tombstones, and their own last-write-wins update granularity for
/// folder sync. A task points at its section via <see cref="TaskItem.SectionId"/>.
/// </remarks>
public sealed class Section : RecordBase
{
    /// <summary>The project this section belongs to.</summary>
    public Guid ProjectId { get; set; }

    /// <summary>Display name (the heading text).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Manual ordering weight within the project. Lower sorts first.</summary>
    public double SortOrder { get; set; }
}
