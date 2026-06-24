using Microsoft.UI.Xaml.Data;

namespace Cue;

/// <summary>Maps an expand/collapse flag to its chevron glyph: a down chevron when expanded, a right
/// chevron when collapsed. Used by the 중요도 view's priority-section headers. One-way only.</summary>
public sealed partial class BoolToChevronGlyphConverter : IValueConverter
{
    // Segoe Fluent Icons: ChevronDown (E70D) when expanded / ChevronRight (E76C) when collapsed.
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? "\uE70D" : "\uE76C";

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
