namespace Cue.Domain;

/// <summary>
/// A cross-cutting tag. Unlike the Area → Project → Task containment hierarchy, a label can be
/// applied to tasks anywhere via <see cref="TaskItem.LabelIds"/>.
/// </summary>
public sealed class Label : RecordBase
{
    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Accent color as a hex string (e.g. "#4F8CC9"). <c>null</c> uses the default.</summary>
    public string? Color { get; set; }

    /// <summary>
    /// Manual ordering rank in the label list — a LexoRank-style fractional string (see
    /// <see cref="TaskItem.SortOrder"/>). Assigned by the store; the domain only holds it.
    /// </summary>
    public string SortOrder { get; set; } = string.Empty;
}
