using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Cue.Behaviors;

/// <summary>
/// App-wide scrollbar auto-hide, applied as an attached behavior so it needs no custom ScrollBar
/// template (the stock template ships as compiled XBF, so retemplating it blind is risky). For a
/// <see cref="ScrollViewer"/> it:
/// <list type="bullet">
///   <item>keeps the bars hidden at rest, fades them in on scroll or hover, and fades them back out a
///   second after the last activity — regardless of the OS "auto-hide scrollbars" setting, which
///   otherwise leaves them permanently visible; and</item>
///   <item>holds the bar in its full-thickness "indicator" form for the whole reveal, so it doesn't
///   collapse to the thin resting pill the framework reverts to shortly after a scroll (which made the
///   bar look thin during the idle moment before it faded out).</item>
/// </list>
/// Attach with <c>behaviors:ScrollBarAutoHide.IsEnabled="True"</c> on a ScrollViewer, or on a control
/// that hosts one (e.g. a ListView or NavigationView) to reach its inner ScrollViewer(s).
/// </summary>
public static class ScrollBarAutoHide
{
    // Idle gap before the bars fade, and the fade in / out durations.
    private static readonly TimeSpan IdleBeforeHide = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(360);

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

    /// <summary>Collects a ScrollViewer's <i>own</i> two scrollbars — walking its template but stopping
    /// at the content presenter, so inner controls' scrollbars (e.g. a TextBox's) are left alone.</summary>
    private static void CollectOwnScrollBars(DependencyObject root, List<ScrollBar> into)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollBar bar) { into.Add(bar); continue; }
            if (child is ScrollContentPresenter) continue; // the scrolled content lives here — don't descend
            CollectOwnScrollBars(child, into);
        }
    }

    /// <summary>Per-ScrollViewer controller: tracks scroll activity and pointer hover, drives the bars'
    /// composition opacity, and holds them in the thick indicator form while shown. Composition opacity
    /// (not Visibility) is used so a hidden bar still hit-tests — moving the pointer onto its strip
    /// brings it back so it can be grabbed.</summary>
    private sealed class Controller
    {
        private const string IndicatorGroup = "ScrollingIndicatorStates";
        private const string ThickState = "MouseIndicator";
        private const string FullMouseState = "MouseIndicatorFull";
        private const double ScrollBarGutter = 12;

        private readonly ScrollViewer _scrollViewer;
        private readonly DispatcherTimer _idle = new() { Interval = IdleBeforeHide };
        private readonly List<ScrollBar> _bars = new();
        private readonly HashSet<ScrollBar> _indicatorHooked = new();
        private readonly Thickness _basePadding;
        private bool _scrollViewerIndicatorHooked;
        private bool _hovering;
        private bool _shown;
        private bool _forcing;

        public Controller(ScrollViewer scrollViewer)
        {
            _scrollViewer = scrollViewer;
            _basePadding = scrollViewer.Padding;
            _idle.Tick += (_, _) =>
            {
                _idle.Stop();
                _shown = false;
                if (!_hovering) Fade(0f, FadeOut);
            };
        }

        public void Attach()
        {
            ReserveScrollBarGutter();
            RefreshBars();

            _scrollViewer.ViewChanged += (_, _) => Bump();
            _scrollViewer.SizeChanged += (_, _) =>
            {
                ReserveScrollBarGutter();
                RefreshBars();
                if (!_shown) Fade(0f, TimeSpan.Zero);
            };
            _scrollViewer.Unloaded += (_, _) => _idle.Stop();

            HookIndicators();
            // Parts realize only after the first layout pass, so hook again once it settles.
            _scrollViewer.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    RefreshBars();
                    HookIndicators();
                    if (!_shown) Fade(0f, TimeSpan.Zero);
                });

            // Start hidden; the first scroll or hover reveals them.
            Fade(0f, TimeSpan.Zero);
        }

        private void ReserveScrollBarGutter()
        {
            var right = _scrollViewer.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled
                ? _basePadding.Right
                : Math.Max(_basePadding.Right, ScrollBarGutter);
            var bottom = _scrollViewer.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled
                ? _basePadding.Bottom
                : Math.Max(_basePadding.Bottom, ScrollBarGutter);

            var reserved = new Thickness(_basePadding.Left, _basePadding.Top, right, bottom);
            if (_scrollViewer.Padding != reserved)
                _scrollViewer.Padding = reserved;
        }

        // Scroll or pointer-leave: reveal, then restart the idle countdown.
        private void Bump()
        {
            Show();
            _idle.Stop();
            _idle.Start();
        }

        private void Show()
        {
            _shown = true;
            RefreshBars();
            HookIndicators();
            ForceThick();
            Fade(1f, FadeIn);
        }

        // Hold the bar in its thicker indicator form: the framework reverts to a thin resting pill a
        // moment after scrolling stops, but we keep showing the bar for a second after that, and it
        // should stay full-thickness for that whole reveal — not thin out just before it fades.
        private void ForceThick()
        {
            _forcing = true;
            VisualStateManager.GoToState(_scrollViewer, ThickState, true);
            foreach (var bar in _bars)
            {
                bar.IndicatorMode = ScrollingIndicatorMode.MouseIndicator;
                VisualStateManager.GoToState(bar, ThickState, true);
            }
            _forcing = false;
        }

        private void RefreshBars()
        {
            var found = new List<ScrollBar>();
            CollectOwnScrollBars(_scrollViewer, found);
            foreach (var bar in found)
            {
                if (_bars.Contains(bar)) continue;
                _bars.Add(bar);
                bar.PointerEntered += (_, _) => { _hovering = true; _idle.Stop(); Show(); };
                bar.PointerExited += (_, _) => { _hovering = false; Bump(); };

                var visual = ElementCompositionPreview.GetElementVisual(bar);
                visual.Opacity = _shown ? 1f : 0f;
                if (_shown) bar.IndicatorMode = ScrollingIndicatorMode.MouseIndicator;
            }
        }

        // If the framework drops the scroll surface or a bar back to touch/thin/no-indicator while
        // we're still showing it, pull it back to the mouse indicator. Guarded so our own state
        // changes can't recurse.
        private void HookIndicators()
        {
            HookScrollViewerIndicator();
            foreach (var bar in _bars)
            {
                if (_indicatorHooked.Contains(bar)) continue;
                if (VisualTreeHelper.GetChildrenCount(bar) == 0 ||
                    VisualTreeHelper.GetChild(bar, 0) is not FrameworkElement root)
                    continue;
                foreach (var group in VisualStateManager.GetVisualStateGroups(root))
                {
                    if (group.Name != IndicatorGroup) continue;
                    var target = bar;
                    group.CurrentStateChanged += (_, e) =>
                    {
                        if (_shown && !_forcing && e.NewState?.Name != ThickState)
                        {
                            target.IndicatorMode = ScrollingIndicatorMode.MouseIndicator;
                            VisualStateManager.GoToState(target, ThickState, true);
                        }
                    };
                    _indicatorHooked.Add(bar);
                }
            }
        }

        private void HookScrollViewerIndicator()
        {
            if (_scrollViewerIndicatorHooked ||
                VisualTreeHelper.GetChildrenCount(_scrollViewer) == 0 ||
                VisualTreeHelper.GetChild(_scrollViewer, 0) is not FrameworkElement root)
                return;

            foreach (var group in VisualStateManager.GetVisualStateGroups(root))
            {
                if (group.Name != IndicatorGroup) continue;
                group.CurrentStateChanged += (_, e) =>
                {
                    if (!_shown || _forcing) return;
                    var stateName = e.NewState?.Name;
                    if (stateName is ThickState or FullMouseState) return;
                    ForceThick();
                };
                _scrollViewerIndicatorHooked = true;
                break;
            }
        }

        private void Fade(float opacity, TimeSpan duration)
        {
            RefreshBars();
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
    }
}
