using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Cue;

/// <summary>Maps a boolean to <see cref="Visibility"/> (true → Visible). One-way only.</summary>
public sealed partial class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
