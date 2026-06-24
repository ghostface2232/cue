using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Cue;

/// <summary>
/// Maps a "#RRGGBB" / "#AARRGGBB" hex string to a <see cref="SolidColorBrush"/> for a tag's label/icon
/// color. In Light mode a perceptually bright color (yellow, cyan, light green) is darkened so it stays
/// legible against the near-white card and its own pale chip tint; Dark mode uses the color as authored,
/// where it already reads well on the dark surface. A null/blank or unparseable value falls back to the
/// secondary text brush (an uncolored label). One-way only.
/// </summary>
public sealed partial class HexToBrushConverter : IValueConverter
{
    // In Light mode, cap a color's perceived luminance (0–255, Rec. 601 weights) to this and scale the
    // color down toward black when it exceeds it. Tuned so yellow darkens to a readable gold while the
    // already-dim hues (red, blue, purple) are left essentially untouched.
    private const double LightModeLuminanceCap = 130;

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string hex && TryParse(hex, out var color))
            return new SolidColorBrush(IsLightTheme() ? DarkenForLight(color) : color);
        return Application.Current.Resources["TextFillColorSecondaryBrush"];
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();

    /// <summary>True when the app is currently showing the Light theme (including while following the
    /// system), read from the live window root's resolved <see cref="FrameworkElement.ActualTheme"/>.</summary>
    private static bool IsLightTheme()
        => App.CurrentWindow?.Content is FrameworkElement root && root.ActualTheme == ElementTheme.Light;

    /// <summary>Scales a color toward black when its perceived luminance exceeds
    /// <see cref="LightModeLuminanceCap"/>, preserving hue. A color already at or below the cap is returned
    /// unchanged, so only the bright end of the palette is darkened.</summary>
    private static Color DarkenForLight(Color color)
    {
        var luminance = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B;
        if (luminance <= LightModeLuminanceCap) return color;
        var scale = LightModeLuminanceCap / luminance;
        return Color.FromArgb(
            color.A,
            (byte)Math.Round(color.R * scale),
            (byte)Math.Round(color.G * scale),
            (byte)Math.Round(color.B * scale));
    }

    private static bool TryParse(string hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.TrimStart('#');
        if (s.Length != 6 && s.Length != 8) return false;
        if (!uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var v))
            return false;
        byte a = s.Length == 8 ? (byte)(v >> 24) : (byte)0xFF;
        byte r = (byte)((v >> 16) & 0xFF);
        byte g = (byte)((v >> 8) & 0xFF);
        byte b = (byte)(v & 0xFF);
        color = Color.FromArgb(a, r, g, b);
        return true;
    }
}
