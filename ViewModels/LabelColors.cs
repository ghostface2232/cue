namespace Cue.ViewModels;

/// <summary>
/// The fixed label color palette. Labels store a hex string (<c>Label.Color</c>); new labels get the
/// next palette color so they are never colorless, and the user can re-pick from this set.
/// </summary>
public static class LabelColors
{
    /// <summary>The palette as (hex, Korean name) pairs — used to build color pickers.</summary>
    public static readonly IReadOnlyList<(string Hex, string Name)> Swatches = new[]
    {
        ("#E74C3C", "빨강"),
        ("#E67E22", "주황"),
        ("#F1C40F", "노랑"),
        ("#2ECC71", "초록"),
        ("#1ABC9C", "청록"),
        ("#3498DB", "파랑"),
        ("#9B59B6", "보라"),
        ("#7F8C8D", "회색"),
    };

    public static readonly IReadOnlyList<string> Palette = Swatches.Select(s => s.Hex).ToArray();

    /// <summary>A default color for a newly created label, cycling through the palette by count.</summary>
    public static string ForNewLabel(int existingCount)
        => Palette[((existingCount % Palette.Count) + Palette.Count) % Palette.Count];
}
