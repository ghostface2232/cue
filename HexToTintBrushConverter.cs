using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Cue;

/// <summary>
/// Maps a "#RRGGBB" / "#AARRGGBB" hex string to a soft, low-opacity tint of that color — the fill
/// behind a tag chip, with the saturated tag color (<see cref="HexToBrushConverter"/>) reading on top.
/// Mirrors <see cref="PriorityToTintBrushConverter"/> for the priority pill. A null/blank or
/// unparseable value falls back to a neutral control fill so an uncolored tag still reads as a chip.
/// One-way only.
/// </summary>
public sealed partial class HexToTintBrushConverter : IValueConverter
{
    private const byte TintAlpha = 0x2B; // ~17% — matches the priority pill tint (PriorityToTintBrushConverter)

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string hex && TryParse(hex, out var color))
            return new SolidColorBrush(Color.FromArgb(TintAlpha, color.R, color.G, color.B));
        return Application.Current.Resources["ControlFillColorSecondaryBrush"];
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();

    private static bool TryParse(string hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.TrimStart('#');
        if (s.Length != 6 && s.Length != 8) return false;
        if (!uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var v))
            return false;
        byte r = (byte)((v >> 16) & 0xFF);
        byte g = (byte)((v >> 8) & 0xFF);
        byte b = (byte)(v & 0xFF);
        color = Color.FromArgb(0xFF, r, g, b);
        return true;
    }
}
