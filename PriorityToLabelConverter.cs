using Cue.Domain;
using Microsoft.UI.Xaml.Data;

namespace Cue;

/// <summary>
/// Maps a task <see cref="Priority"/> to its Korean 중요도 (importance) label for display in the
/// detail editor's dropdown. One-way only.
/// </summary>
public sealed partial class PriorityToLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is Priority priority
            ? priority switch
            {
                Priority.P1 => "매우 중요",
                Priority.P2 => "중요",
                Priority.P3 => "보통",
                Priority.P4 => "사소",
                _ => "없음",
            }
            : "없음";

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
