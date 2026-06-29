using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Cue.Services;

/// <summary>
/// Resolves theme-dependent resources — and themes pop-up content — against the app's <i>in-app</i>
/// theme (the window root's <see cref="ElementTheme"/> override) rather than the OS
/// <see cref="ApplicationTheme"/>.
///
/// <para>Reading a brush straight from <c>Application.Current.Resources</c> returns the value for
/// <see cref="Application.RequestedTheme"/>, which always follows the OS. So anything resolved that way —
/// the converters, code-built flyout content, imperatively-tinted glyphs — stays in the OS theme even
/// after the user switches Cue to a different mode in 설정 &gt; 화면 모드, and only flips once the OS itself
/// changes. Likewise, a <see cref="FlyoutBase"/>'s content lives in the pop-up root and does <b>not</b>
/// inherit the window root's <c>RequestedTheme</c>. Route every code-side themed lookup and every
/// pop-up through here so the whole UI follows the in-app choice. Mirrors the live-theme read in
/// <see cref="Cue.HexToBrushConverter"/>.</para>
/// </summary>
internal static class ThemeResources
{
    /// <summary>The app's effective theme — the window root's resolved
    /// <see cref="FrameworkElement.ActualTheme"/> (Light or Dark). Falls back to the OS application
    /// theme before the window root exists.</summary>
    public static ElementTheme Effective
    {
        get
        {
            if (App.CurrentWindow?.Content is FrameworkElement root && root.ActualTheme != ElementTheme.Default)
                return root.ActualTheme;
            return Application.Current.RequestedTheme == ApplicationTheme.Dark
                ? ElementTheme.Dark
                : ElementTheme.Light;
        }
    }

    /// <summary>The ThemeDictionaries key for the effective theme — high contrast wins over light/dark,
    /// matching how the framework picks a theme dictionary.</summary>
    private static string ThemeDictionaryKey()
        => AppPreferences.IsHighContrast() ? "HighContrast"
            : Effective == ElementTheme.Dark ? "Dark"
            : "Light";

    /// <summary>Resolves a themed brush for the app's effective theme. Looks the key up in the app's own
    /// <c>ThemeDictionaries</c> (DesignTokens.xaml) so it returns the right light/dark/high-contrast
    /// instance regardless of the OS theme; falls back to the app-level (OS-theme) resource for keys not
    /// split there. Null only if the key is absent everywhere.</summary>
    public static Brush? Brush(string key)
    {
        var themeKey = ThemeDictionaryKey();
        foreach (var merged in Application.Current.Resources.MergedDictionaries)
        {
            if (merged.ThemeDictionaries.TryGetValue(themeKey, out var themed)
                && themed is ResourceDictionary dict
                && dict.TryGetValue(key, out var value)
                && value is Brush brush)
                return brush;
        }
        return Application.Current.Resources.TryGetValue(key, out var fallback) && fallback is Brush fb
            ? fb
            : null;
    }

    /// <summary>Forces a pop-up's content to the app's effective theme. Flyout content is hosted in the
    /// pop-up root and does not inherit the window root's theme override, so without this its
    /// <c>{ThemeResource}</c> lookups and default control colors resolve to the OS theme.</summary>
    public static void Apply(FrameworkElement? content)
    {
        if (content is not null)
            content.RequestedTheme = Effective;
    }

    /// <summary>Themes a <see cref="MenuFlyout"/>: pins the effective theme on every item up front (so a
    /// check glyph or sub-menu carries it) and on the presenter the moment it opens (so the menu surface,
    /// separators, and default text follow the in-app theme too).</summary>
    public static void Apply(MenuFlyout menu)
    {
        ApplyToItems(menu.Items);
        menu.Opened += static (sender, _) =>
        {
            if (sender is MenuFlyout opened && opened.Items.Count > 0
                && PresenterFor(opened.Items[0]) is { } presenter)
                presenter.RequestedTheme = Effective;
        };
    }

    private static void ApplyToItems(IList<MenuFlyoutItemBase> items)
    {
        var theme = Effective;
        foreach (var item in items)
        {
            item.RequestedTheme = theme;
            if (item is MenuFlyoutSubItem sub)
                ApplyToItems(sub.Items);
        }
    }

    /// <summary>Walks up from a realized menu item to its hosting <see cref="MenuFlyoutPresenter"/>.</summary>
    private static FrameworkElement? PresenterFor(MenuFlyoutItemBase item)
    {
        DependencyObject? node = item;
        while (node is not null)
        {
            if (node is MenuFlyoutPresenter presenter)
                return presenter;
            node = VisualTreeHelper.GetParent(node);
        }
        return item;
    }
}
