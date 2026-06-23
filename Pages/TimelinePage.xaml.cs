using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;
using Windows.UI.ViewManagement;
using Cue.ViewModels;

namespace Cue.Pages;

/// <summary>A horizontally scrolling month timeline over the index-backed task date projection.</summary>
public sealed partial class TimelinePage : Page
{
    private const double KeyboardScrollStep = 440;
    private const double WheelScrollMultiplier = 2.0;

    private readonly bool _animationsEnabled = new UISettings().AnimationsEnabled;
    private bool _isPointerPanning;
    private uint _panPointerId;
    private double _panStartX;
    private double _panStartY;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;

    public TimelineViewModel ViewModel { get; }

    public TimelinePage()
    {
        ViewModel = App.Services.GetRequiredService<TimelineViewModel>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await RunSafelyAsync(async () =>
        {
            await ViewModel.LoadCommand.ExecuteAsync(null);
            CenterTodayInView();
        });
    }

    private async void PreviousMonth_Click(object sender, RoutedEventArgs e)
        => await RunSafelyAsync(async () =>
        {
            await ViewModel.PreviousMonthCommand.ExecuteAsync(null);
            FocusTimeline();
        });

    private async void Today_Click(object sender, RoutedEventArgs e)
        => await RunSafelyAsync(async () =>
        {
            await ViewModel.GoTodayCommand.ExecuteAsync(null);
            CenterTodayInView();
        });

    private async void NextMonth_Click(object sender, RoutedEventArgs e)
        => await RunSafelyAsync(async () =>
        {
            await ViewModel.NextMonthCommand.ExecuteAsync(null);
            FocusTimeline();
        });

    private void TimelineScrollViewer_Loaded(object sender, RoutedEventArgs e)
    {
        FocusTimeline();
        CenterTodayInView();
    }

    private void TimelineScrollViewer_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var delta = e.Key switch
        {
            VirtualKey.Left => -KeyboardScrollStep,
            VirtualKey.Right => KeyboardScrollStep,
            _ => 0,
        };
        if (delta == 0) return;

        e.Handled = true;
        ScrollBy(delta, 0, disableAnimation: false);
    }

    private void TimelineScrollViewer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(TimelineScrollViewer);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        _isPointerPanning = true;
        _panPointerId = point.PointerId;
        _panStartX = point.Position.X;
        _panStartY = point.Position.Y;
        _panStartHorizontalOffset = TimelineScrollViewer.HorizontalOffset;
        _panStartVerticalOffset = TimelineScrollViewer.VerticalOffset;
        TimelineScrollViewer.CapturePointer(e.Pointer);
        FocusTimeline();
        e.Handled = true;
    }

    private void TimelineScrollViewer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPointerPanning)
            return;

        var point = e.GetCurrentPoint(TimelineScrollViewer);
        if (point.PointerId != _panPointerId || !point.Properties.IsLeftButtonPressed)
        {
            EndPointerPan(e);
            return;
        }

        TimelineScrollViewer.ChangeView(
            ClampOffset(_panStartHorizontalOffset - (point.Position.X - _panStartX), TimelineScrollViewer.ScrollableWidth),
            ClampOffset(_panStartVerticalOffset - (point.Position.Y - _panStartY), TimelineScrollViewer.ScrollableHeight),
            null,
            disableAnimation: true);
        e.Handled = true;
    }

    private void TimelineScrollViewer_PointerReleased(object sender, PointerRoutedEventArgs e)
        => EndPointerPan(e);

    private void TimelineScrollViewer_PointerCanceled(object sender, PointerRoutedEventArgs e)
        => EndPointerPan(e);

    private void TimelineScrollViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(TimelineScrollViewer).Properties.MouseWheelDelta;
        if (delta == 0)
            return;

        e.Handled = true;
        ScrollBy(-delta * WheelScrollMultiplier, 0, disableAnimation: false);
    }

    private void EndPointerPan(PointerRoutedEventArgs e)
    {
        if (!_isPointerPanning)
            return;

        _isPointerPanning = false;
        TimelineScrollViewer.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void CenterTodayInView()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            TimelineScrollViewer.UpdateLayout();
            FocusTimeline();
            if (!ViewModel.HasTodayInRange)
                return;

            var target = ViewModel.TodayLineOffset - (TimelineScrollViewer.ViewportWidth / 2);
            TimelineScrollViewer.ChangeView(
                ClampOffset(target, TimelineScrollViewer.ScrollableWidth),
                null,
                null,
                disableAnimation: false);
        });
    }

    private void FocusTimeline()
        => TimelineScrollViewer.Focus(FocusState.Programmatic);

    private void ScrollBy(double horizontalDelta, double verticalDelta, bool disableAnimation)
    {
        TimelineScrollViewer.ChangeView(
            ClampOffset(TimelineScrollViewer.HorizontalOffset + horizontalDelta, TimelineScrollViewer.ScrollableWidth),
            ClampOffset(TimelineScrollViewer.VerticalOffset + verticalDelta, TimelineScrollViewer.ScrollableHeight),
            null,
            disableAnimation);
    }

    private static double ClampOffset(double value, double maximum)
        => Math.Min(Math.Max(0, value), Math.Max(0, maximum));

    private void TimelineBar_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border border) return;
        border.Background = (Microsoft.UI.Xaml.Media.Brush)Resources["TimelineRowHoverBrush"];
        if (!_animationsEnabled) return;
        var visual = ElementCompositionPreview.GetElementVisual(border);
        visual.Scale = new Vector3(1.0025f, 1.0025f, 1f);
    }

    private void TimelineBar_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border border) return;
        border.Background = (Microsoft.UI.Xaml.Media.Brush)Resources["TimelineBarBrush"];
        ElementCompositionPreview.GetElementVisual(border).Scale = Vector3.One;
    }

    private void TimelineRows_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (!_animationsEnabled || args.Element is not UIElement element) return;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Scale");
        visual.Opacity = 1f;
        visual.Scale = Vector3.One;

        var delay = TimeSpan.FromMilliseconds(Math.Min(args.Index, 8) * 20);
        var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0f, 0f);
        opacity.InsertKeyFrame(1f, 1f);
        opacity.Duration = TimeSpan.FromMilliseconds(160);
        opacity.DelayTime = delay;

        var scale = visual.Compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0f, new Vector3(0.994f, 0.982f, 1f));
        scale.InsertKeyFrame(1f, Vector3.One);
        scale.Duration = TimeSpan.FromMilliseconds(200);
        scale.DelayTime = delay;

        visual.StartAnimation("Opacity", opacity);
        visual.StartAnimation("Scale", scale);
    }

    private void TimelineRows_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        var visual = ElementCompositionPreview.GetElementVisual(args.Element);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Scale");
        visual.Opacity = 1f;
        visual.Scale = Vector3.One;
    }

    private async Task RunSafelyAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            ErrorInfoBar.Message = exception.Message;
            ErrorInfoBar.IsOpen = true;
        }
    }
}
