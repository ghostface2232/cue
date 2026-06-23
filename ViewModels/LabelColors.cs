namespace Cue.ViewModels;

/// <summary>
/// The fixed label color palette. Labels store a hex string (<c>Label.Color</c>); new labels get the
/// next palette color so they are never colorless, and the user can re-pick from this set.
/// </summary>
public static class LabelColors
{
    public static readonly IReadOnlyList<string> Palette = new[]
    {
        "#E74C3C", // red
        "#E67E22", // orange
        "#F1C40F", // amber
        "#2ECC71", // green
        "#1ABC9C", // teal
        "#3498DB", // blue
        "#9B59B6", // purple
        "#7F8C8D", // gray
    };

    /// <summary>A default color for a newly created label, cycling through the palette by count.</summary>
    public static string ForNewLabel(int existingCount)
        => Palette[((existingCount % Palette.Count) + Palette.Count) % Palette.Count];
}
