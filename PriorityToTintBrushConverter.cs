using Cue.Domain;
using Cue.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Cue;

/// <summary>
/// Maps a task <see cref="Priority"/> to a soft, low-opacity tint of its cue color — the fill behind
/// the importance pill on a task row. Resolves the priority color from the same app resources as
/// <see cref="PriorityToBrushConverter"/> (which provides the saturated text color), then drops the
/// alpha so the saturated label reads on top. One-way only.
/// </summary>
public sealed partial class PriorityToTintBrushConverter : IValueConverter
{
    private const byte TintAlpha = 0x2B; // ~17%

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value is Priority priority
            ? priority switch
            {
                Priority.P1 => "CuePriorityP1Brush",
                Priority.P2 => "CuePriorityP2Brush",
                Priority.P3 => "CuePriorityP3Brush",
                Priority.P4 => "CuePriorityP4Brush",
                _ => null,
            }
            : null;

        if (key is not null
            && ThemeResources.Brush(key) is SolidColorBrush brush)
        {
            var color = brush.Color;
            return new SolidColorBrush(Windows.UI.Color.FromArgb(TintAlpha, color.R, color.G, color.B));
        }
        return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
