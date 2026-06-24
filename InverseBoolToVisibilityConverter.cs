using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Cue;

/// <summary>Maps a boolean to <see cref="Visibility"/> inverted (true → Collapsed, false → Visible).
/// One-way only.</summary>
public sealed partial class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
