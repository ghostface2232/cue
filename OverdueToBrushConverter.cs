using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Cue.Services;

namespace Cue;

/// <summary>
/// Maps a task row's <c>IsOverdue</c> flag to the schedule line's foreground: the overdue tone when the
/// work is past due, the ordinary secondary text tone otherwise.
/// </summary>
/// <remarks>
/// The view models are a plain net10.0 project with no WinUI reference, so they carry the boolean and the
/// brush choice lands here. Resolution goes through <see cref="ThemeResources"/> rather than
/// <c>Application.Current.Resources</c> so the color follows the app's in-app theme (설정 &gt; 화면 모드)
/// instead of the OS theme — the same rule every other code-side themed lookup follows.
/// </remarks>
public sealed class OverdueToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value is true ? "CueOverdueBrush" : "CueGlyphSecondaryBrush";
        if (ThemeResources.Brush(key) is { } brush)
            return brush;
        // Never hand back null: a null Foreground would fall through to the inherited value and could make
        // an overdue row read as ordinary. Transparent would hide the text, so use the caller's default.
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
