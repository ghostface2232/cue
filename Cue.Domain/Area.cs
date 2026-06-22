namespace Cue.Domain;

/// <summary>
/// An area of responsibility — the top of the hierarchy. Groups <see cref="Project"/>s and
/// can also hold loose <see cref="TaskItem"/>s directly. Tasks/projects with no area are
/// simply unclassified.
/// </summary>
public sealed class Area : RecordBase
{
    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Accent color as a hex string (e.g. "#4F8CC9"). <c>null</c> uses the default.</summary>
    public string? Color { get; set; }

    /// <summary>Manual ordering weight in the sidebar. Lower sorts first.</summary>
    public double SortOrder { get; set; }
}
