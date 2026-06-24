using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Cue.Behaviors;

/// <summary>
/// App-wide scrollbar polish, applied as an attached behavior so it needs no custom ScrollBar
/// template (the stock template ships as compiled XBF, so retemplating it blind is risky). It reaches
/// into a <see cref="ScrollViewer"/>'s realized scrollbar parts to:
/// <list type="bullet">
///   <item>nudge the thumb a touch thicker (the fluent default reads too thin), and</item>
///   <item>keep the bars hidden at rest, fade them in on scroll or hover, and fade them back out after
///   a short idle — regardless of the OS "auto-hide scrollbars" setting, which otherwise leaves them
///   permanently visible.</item>
/// </list>
/// Attach with <c>behaviors:ScrollBarAutoHide.IsEnabled="True"</c> on a ScrollViewer, or on a control
/// that hosts one (e.g. a ListView) to reach its inner ScrollViewer.
/// </summary>
public static class ScrollBarAutoHide
{
    // Idle gap before the bars fade, and the fade in / out durations. Motion, not metric — kept here
    // rather than as design tokens. The thumb thickness IS a token (CueScrollBarThumbThickness).
    private static readonly TimeSpan IdleBeforeHide = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(360);
    private const double FallbackThumbThickness = 8;

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(ScrollBarAutoHide), new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    // One controller per ScrollViewer; the table lets the controller die with its ScrollViewer.
    private static readonly ConditionalWeakTable<ScrollViewer, Controller> Controllers = new();

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || !(bool)e.NewValue) return;
        if (element.IsLoaded) Hook(element);
        else element.Loaded += OnElementLoaded;
    }

    private static void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        element.Loaded -= OnElementLoaded;
        Hook(element);
    }

    private static void Hook(FrameworkElement element)
    {
        // The behavior may sit on the ScrollViewer itself, or on a host whose template owns one or more
        // (a ListView has one; a NavigationView has several). Wiring is idempotent — the weak table
        // skips any ScrollViewer already managed — so a host and an inner element can both opt in.
        if (element is ScrollViewer scrollViewer)
        {
            Wire(scrollViewer);
            return;
        }
        var found = new List<ScrollViewer>();
        CollectDescendants(element, found);
        foreach (var inner in found) Wire(inner);
    }

    private static void Wire(ScrollViewer scrollViewer)
    {
        if (Controllers.TryGetValue(scrollViewer, out _)) return;
        var controller = new Controller(scrollViewer);
        Controllers.Add(scrollViewer, controller);
        controller.Attach();
    }

    private static double ThumbThickness =>
        Application.Current.Resources.TryGetValue("CueScrollBarThumbThickness", out var value) && value is double thickness
            ? thickness
            : FallbackThumbThickness;

    private static T? FindDescendant<T>(DependencyObject root) where T : class
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static void CollectDescendants<T>(DependencyObject root, List<T> into) where T : class
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) into.Add(match);
            CollectDescendants(child, into);
        }
    }

    /// <summary>Per-ScrollViewer fade controller: tracks scroll activity and pointer hover, and drives
    /// the bars' composition opacity. Composition opacity (not Visibility) is used so a hidden bar still
    /// hit-tests — moving the pointer onto its strip brings it back so it can be grabbed.</summary>
    private sealed class Controller
    {
        private readonly ScrollViewer _scrollViewer;
        private readonly DispatcherTimer _idle = new() { Interval = IdleBeforeHide };
        private readonly List<ScrollBar> _bars = new();
        private bool _hovering;

        public Controller(ScrollViewer scrollViewer)
        {
            _scrollViewer = scrollViewer;
            _idle.Tick += (_, _) =>
            {
                _idle.Stop();
                if (!_hovering) Fade(0f, FadeOut);
            };
        }

        public void Attach()
        {
            CollectDescendants(_scrollViewer, _bars);
            foreach (var bar in _bars)
            {
                ApplyThickness(bar);
                bar.PointerEntered += (_, _) => { _hovering = true; _idle.Stop(); Fade(1f, FadeIn); };
                bar.PointerExited += (_, _) => { _hovering = false; Bump(); };
            }

            _scrollViewer.ViewChanged += (_, _) => Bump();
            _scrollViewer.Unloaded += (_, _) => _idle.Stop();

            // Start hidden; the first scroll or hover reveals them.
            Fade(0f, TimeSpan.Zero);
        }

        // Scroll or pointer-leave: ensure visible, then restart the idle countdown.
        private void Bump()
        {
            Fade(1f, FadeIn);
            _idle.Stop();
            _idle.Start();
        }

        private void Fade(float opacity, TimeSpan duration)
        {
            foreach (var bar in _bars)
            {
                var visual = ElementCompositionPreview.GetElementVisual(bar);
                if (duration == TimeSpan.Zero)
                {
                    visual.Opacity = opacity;
                    continue;
                }
                var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
                animation.InsertKeyFrame(1f, opacity);
                animation.Duration = duration;
                visual.StartAnimation("Opacity", animation);
            }
        }

        private static void ApplyThickness(ScrollBar bar)
        {
            // The thumb may not be realized until the bar loads; defer if so.
            if (FindDescendant<Thumb>(bar) is not { } thumb)
            {
                void OnLoaded(object s, RoutedEventArgs e)
                {
                    bar.Loaded -= OnLoaded;
                    ApplyThickness(bar);
                }
                bar.Loaded += OnLoaded;
                return;
            }

            // Raise the floor (MinWidth/MinHeight) rather than pin Width/Height, so the template's own
            // hover-expand still works — the thumb just never gets thinner than this.
            if (bar.Orientation == Orientation.Vertical)
                thumb.MinWidth = ThumbThickness;
            else
                thumb.MinHeight = ThumbThickness;
        }
    }
}
