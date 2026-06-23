using Cue.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Cue;

/// <summary>
/// Maps a task <see cref="Priority"/> to its cue brush (the small dot on a task row). Resolves the
/// theme-aware brush from the app resources (defined in DesignTokens.xaml) so it flips light/dark.
/// One-way only.
/// </summary>
public sealed partial class PriorityToBrushConverter : IValueConverter
{
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

        if (key is not null && Application.Current.Resources.TryGetValue(key, out var brush) && brush is Brush)
            return brush;
        return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
