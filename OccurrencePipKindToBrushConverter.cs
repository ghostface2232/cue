using Cue.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Cue;

/// <summary>
/// Maps a recurrence timeline pip's <see cref="OccurrencePipKind"/> to its glyph brush. Resolves the
/// theme-aware Cue timeline brush from app resources (defined in DesignTokens.xaml) so it flips
/// light/dark. The color is a secondary cue only — each pip also carries a distinct glyph, a status
/// label, and an automation name/tooltip — so this never makes status color-dependent. One-way only.
/// </summary>
public sealed partial class OccurrencePipKindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value is OccurrencePipKind kind
            ? kind switch
            {
                OccurrencePipKind.Completed => "CueTimelineCompletedBrush",
                OccurrencePipKind.Current => "CueTimelineCurrentBrush",
                OccurrencePipKind.Future => "CueTimelineFutureBrush",
                OccurrencePipKind.Ended => "CueTimelineEndedBrush",
                OccurrencePipKind.Missed => "CueTimelineMutedBrush",
                _ => "CueTimelineMutedBrush",
            }
            : "CueTimelineMutedBrush";

        if (Application.Current.Resources.TryGetValue(key, out var brush) && brush is Brush)
            return brush;
        return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
