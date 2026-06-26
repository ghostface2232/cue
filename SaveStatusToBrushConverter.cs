using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Cue.ViewModels;

namespace Cue;

/// <summary>
/// Maps a <see cref="SaveStatus"/> to a theme-aware Brush.
/// </summary>
public sealed class SaveStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is SaveStatus status)
        {
            var key = status switch
            {
                SaveStatus.Saving => "CueSavingBrush",
                SaveStatus.Failed => "CueDangerFillBrush",
                _ => null
            };

            if (key is not null && Application.Current.Resources.TryGetValue(key, out var brush) && brush is Brush)
                return brush;
        }
        return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
