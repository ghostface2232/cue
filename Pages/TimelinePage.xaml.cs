using System.Numerics;
using Cue.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI.ViewManagement;
using Cue.ViewModels;

namespace Cue.Pages;

/// <summary>A horizontally scrolling month timeline over the index-backed task date projection.</summary>
public sealed partial class TimelinePage : Page
{
    private const double DetailDefaultWidth = 460;
    private const double DetailMinWidth = 320;
    private const double DetailAbsoluteMinWidth = 260;
    private const double DetailMaxWidth = 680;
    private const double DetailPrimaryMinWidth = 340;
    private const double DetailCompactBreakpoint = 390;
    // Below this content width the detail panel switches from a side-by-side column to a full-width
    // overlay over the timeline (matching TaskListPage). The hysteresis band prevents edge chatter.
    private const double SideBySideMinWidth = 600;
    private const double LayoutHysteresis = 24;
    private const double KeyboardScrollStep = 440;
    private const double WheelScrollMultiplier = 2.0;

    private readonly bool _animationsEnabled = new UISettings().AnimationsEnabled;
    private bool _isPointerPanning;
    private uint _panPointerId;
    private double _panStartX;
    private double _panStartY;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;
    private bool _panMoved;
    private Visual? _detailPanelVisual;
    private bool _isResizingDetail;
    private double _detailPreferredWidth = DetailDefaultWidth;
    private double _resizeStartX;
    private double _resizeStartWidth;
    private bool _detailOverlay;

    private readonly DialogService _dialogs;
    private readonly INavDataChangeNotifier _navNotifier;
    public TimelineViewModel ViewModel { get; }

    public TimelinePage()
    {
        ViewModel = App.Services.GetRequiredService<TimelineViewModel>();
        _dialogs = App.Services.GetRequiredService<DialogService>();
        _navNotifier = App.Services.GetRequiredService<INavDataChangeNotifier>();
        InitializeComponent();
        // Reflect groups/tags created elsewhere (the sidebar, another panel) in this panel's option
        // lists at once. Unsubscribed on navigate-away (the Frame discards the page).
        _navNotifier.Changed += OnNavDataChanged;
    }

    private async void OnNavDataChanged(object? sender, EventArgs e)
        => await RunSafelyAsync(() => ViewModel.Detail.ReloadNavOptionsAsync());

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _navNotifier.Changed -= OnNavDataChanged;
        base.OnNavigatedFrom(e);
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
        _panMoved = false;
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

        var deltaX = point.Position.X - _panStartX;
        var deltaY = point.Position.Y - _panStartY;
        if (Math.Abs(deltaX) > 4 || Math.Abs(deltaY) > 4)
            _panMoved = true;

        TimelineScrollViewer.ChangeView(
            ClampOffset(_panStartHorizontalOffset - deltaX, TimelineScrollViewer.ScrollableWidth),
            ClampOffset(_panStartVerticalOffset - deltaY, TimelineScrollViewer.ScrollableHeight),
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

    private async void TimelineBar_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_panMoved)
        {
            _panMoved = false;
            return;
        }

        if (sender is not FrameworkElement { Tag: Guid id })
            return;

        e.Handled = true;
        await RunSafelyAsync(() => ViewModel.SelectTaskCommand.ExecuteAsync(id));
    }

    private void TimelineBar_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border border) return;
        border.Background = (Microsoft.UI.Xaml.Media.Brush)Resources["TimelineRowHoverBrush"];
        SetTitleFadeBrush(border, "TimelineRowHoverBrush");
        if (!_animationsEnabled) return;
        var visual = ElementCompositionPreview.GetElementVisual(border);
        visual.Scale = new Vector3(1.0025f, 1.0025f, 1f);
    }

    private void TimelineBar_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border border) return;
        border.Background = (Microsoft.UI.Xaml.Media.Brush)Resources["TimelineBarBrush"];
        SetTitleFadeBrush(border, "TimelineBarBrush");
        ElementCompositionPreview.GetElementVisual(border).Scale = Vector3.One;
    }

    private void SetTitleFadeBrush(DependencyObject root, string brushKey)
    {
        if (FindDescendant<Rectangle>(root, "TimelineTitleFade") is not { } fade)
            return;
        if (Resources[brushKey] is not SolidColorBrush brush)
            return;

        fade.Fill = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 0),
            GradientStops =
            {
                new GradientStop { Offset = 0, Color = Microsoft.UI.Colors.Transparent },
                new GradientStop { Offset = 0.58, Color = brush.Color },
                new GradientStop { Offset = 1, Color = brush.Color },
            },
        };
    }

    private void FadeText_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock text)
            UpdateTextFade(text);
    }

    private void FadeText_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is TextBlock text)
            UpdateTextFade(text);
    }

    private static void UpdateTextFade(TextBlock text)
    {
        if (VisualTreeHelper.GetParent(text) is not DependencyObject parent)
            return;
        var fadeName = text.Name == "TimelineTitleText" ? "TimelineTitleFade" : "TagNameFade";
        if (FindDescendant<Rectangle>(parent, fadeName) is not { } fade)
            return;

        text.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        fade.Opacity = text.DesiredSize.Width > text.ActualWidth + 1 ? 1 : 0;
    }

    private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T element && element.Name == name)
                return element;
            if (FindDescendant<T>(child, name) is { } match)
                return match;
        }
        return null;
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

    private void DetailPanel_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement panel) return;
        ElementCompositionPreview.SetIsTranslationEnabled(panel, true);
        var visual = ElementCompositionPreview.GetElementVisual(panel);
        _detailPanelVisual = visual;
        visual.Opacity = panel.Visibility == Visibility.Visible ? 1f : 0f;
        ApplyDetailPanelWidth();

        panel.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            UpdateContentObscured();
            var shown = panel.Visibility == Visibility.Visible;
            if (!_animationsEnabled)
            {
                visual.Opacity = shown ? 1f : 0f;
                return;
            }
            if (shown)
                AnimateDetailPanelIn(visual);
            else if (visual.Opacity > 0.05f)
                AnimateDetailPanelOut(visual);
        });
    }

    private void ContentSplitGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateSplitLayout();

    private void DetailPanel_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateDetailResponsiveLayout();

    /// <summary>
    /// Picks the master/detail arrangement for the current content width: timeline and panel side by
    /// side when there's room, or the panel as a full-width overlay over the timeline when narrow.
    /// </summary>
    private void UpdateSplitLayout()
    {
        var width = ContentSplitGrid.ActualWidth;
        if (width <= 0)
            return;

        var overlay = _detailOverlay
            ? width < SideBySideMinWidth + LayoutHysteresis
            : width < SideBySideMinWidth;

        if (overlay != _detailOverlay)
            ApplyDetailLayoutMode(overlay);
        else
            ApplyDetailPanelWidth();
    }

    /// <summary>
    /// Moves the detail panel between its side-by-side column and a full-width overlay over the timeline.
    /// The panel's opaque background covers the timeline in overlay mode, so closing it reveals the
    /// timeline again — the window is never resized on the user's behalf.
    /// </summary>
    private void ApplyDetailLayoutMode(bool overlay)
    {
        _detailOverlay = overlay;
        if (overlay)
        {
            Grid.SetColumn(DetailPanel, 0);
            Grid.SetColumnSpan(DetailPanel, 2);
            DetailPanel.Width = double.NaN;
            DetailResizeHitArea.Visibility = Visibility.Collapsed;
            SetDetailResizeGripVisible(false);
        }
        else
        {
            Grid.SetColumn(DetailPanel, 1);
            Grid.SetColumnSpan(DetailPanel, 1);
            DetailResizeHitArea.Visibility = Visibility.Visible;
        }
        UpdateContentObscured();
        ApplyDetailPanelWidth();
    }

    /// <summary>
    /// In overlay mode the open panel covers the timeline. Hide the timeline while it's covered so the
    /// translucent panel composites over the bare page exactly as it does side by side — identical color,
    /// no see-through. Opacity (not Collapsed) preserves the timeline's scroll position.
    /// </summary>
    private void UpdateContentObscured()
    {
        var obscured = _detailOverlay && DetailPanel.Visibility == Visibility.Visible;
        ContentContainer.Opacity = obscured ? 0 : 1;
        ContentContainer.IsHitTestVisible = !obscured;
    }

    private void DetailResizeHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement handle) return;
        var point = e.GetCurrentPoint(ContentSplitGrid);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        _isResizingDetail = true;
        _resizeStartX = point.Position.X;
        _resizeStartWidth = DetailPanel.ActualWidth > 0 ? DetailPanel.ActualWidth : DetailPanel.Width;
        handle.CapturePointer(e.Pointer);
        SetDetailResizeGripVisible(true);
        e.Handled = true;
    }

    private void DetailResizeHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizingDetail)
            return;

        var point = e.GetCurrentPoint(ContentSplitGrid);
        _detailPreferredWidth = _resizeStartWidth - (point.Position.X - _resizeStartX);
        ApplyDetailPanelWidth();
        e.Handled = true;
    }

    private void DetailResizeHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizingDetail)
            return;

        _isResizingDetail = false;
        if (sender is UIElement handle)
            handle.ReleasePointerCapture(e.Pointer);
        SetDetailResizeGripVisible(false);
        e.Handled = true;
    }

    private void DetailResizeHandle_PointerEntered(object sender, PointerRoutedEventArgs e)
        => SetDetailResizeGripVisible(true);

    private void DetailResizeHandle_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizingDetail)
            SetDetailResizeGripVisible(false);
    }

    private void SetDetailResizeGripVisible(bool visible)
    {
        DetailResizeGrip.Opacity = visible ? 0.72 : 0;
    }

    private void ApplyDetailPanelWidth()
    {
        // In overlay mode the panel stretches across both columns (Width = NaN); leave it and only
        // refresh the panel's internal one-/two-column responsive state.
        if (_detailOverlay)
        {
            UpdateDetailResponsiveLayout();
            return;
        }

        if (ContentSplitGrid.ActualWidth <= 0)
            return;

        var width = ClampDetailWidth(_detailPreferredWidth);
        if (Math.Abs(DetailPanel.Width - width) > 0.5)
            DetailPanel.Width = width;
        UpdateDetailResponsiveLayout();
    }

    private double ClampDetailWidth(double desired)
    {
        var maxByWindow = ContentSplitGrid.ActualWidth - DetailPrimaryMinWidth - ContentSplitGrid.ColumnSpacing - 8;
        var max = Math.Min(DetailMaxWidth, Math.Max(DetailAbsoluteMinWidth, maxByWindow));
        var min = Math.Min(DetailMinWidth, max);
        return Math.Clamp(desired, min, max);
    }

    private void UpdateDetailResponsiveLayout()
    {
        var width = DetailPanel.ActualWidth > 0 ? DetailPanel.ActualWidth : DetailPanel.Width;
        var compact = width < DetailCompactBreakpoint;
        SetResponsivePair(DetailMetaGrid, DetailGroupField, compact, new GridLength(1, GridUnitType.Star));
        SetResponsivePair(WhenDateTimeGrid, WhenTimePanel, compact, GridLength.Auto);
    }

    private static void SetResponsivePair(Grid grid, FrameworkElement second, bool compact, GridLength normalSecondWidth)
    {
        while (grid.RowDefinitions.Count < 2)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        if (grid.ColumnDefinitions.Count > 1)
            grid.ColumnDefinitions[1].Width = compact ? new GridLength(0) : normalSecondWidth;
        grid.RowDefinitions[1].Height = compact ? GridLength.Auto : new GridLength(0);

        Grid.SetColumn(second, compact ? 0 : 1);
        Grid.SetRow(second, compact ? 1 : 0);
    }

    private static void AnimateDetailPanelIn(Visual visual)
    {
        var compositor = visual.Compositor;
        var spline = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1.0f));

        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.Target = "Translation";
        slide.InsertKeyFrame(0f, new Vector3(28f, 0f, 0f));
        slide.InsertKeyFrame(1f, Vector3.Zero, spline);
        slide.Duration = TimeSpan.FromMilliseconds(350);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, spline);
        fade.Duration = TimeSpan.FromMilliseconds(280);

        visual.StartAnimation("Translation", slide);
        visual.StartAnimation("Opacity", fade);
    }

    private static void AnimateDetailPanelOut(Visual visual)
    {
        var compositor = visual.Compositor;
        var spline = compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f));

        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.Target = "Translation";
        slide.InsertKeyFrame(0f, Vector3.Zero);
        slide.InsertKeyFrame(1f, new Vector3(24f, 0f, 0f), spline);
        slide.Duration = TimeSpan.FromMilliseconds(180);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        fade.InsertKeyFrame(0f, visual.Opacity);
        fade.InsertKeyFrame(1f, 0f, spline);
        fade.Duration = TimeSpan.FromMilliseconds(160);

        visual.StartAnimation("Translation", slide);
        visual.StartAnimation("Opacity", fade);
    }

    private void EnableWhen_Click(object sender, RoutedEventArgs e) => ViewModel.Detail.EnableWhenEditor();
    private void ClearWhen_Click(object sender, RoutedEventArgs e) => ViewModel.Detail.ClearWhen();
    private async void CloseDetail_Click(object sender, RoutedEventArgs e)
        => await CloseDetailWithAnimationAsync();

    private async Task CloseDetailWithAnimationAsync()
    {
        // The panel autosaves as fields change, but a title/notes edit whose LostFocus hasn't fired yet
        // needs a final flush so closing never drops an in-progress text edit.
        await ViewModel.Detail.FlushAsync();

        if (!_animationsEnabled || _detailPanelVisual is null)
        {
            ViewModel.Detail.Close();
            return;
        }

        AnimateDetailPanelOut(_detailPanelVisual);
        await Task.Delay(170);
        ViewModel.Detail.Close();
    }

    // Title and notes are continuous-typing fields: they autosave on focus-out rather than per keystroke.
    private async void DetailText_LostFocus(object sender, RoutedEventArgs e)
        => await ViewModel.Detail.FlushAsync();

    private async void AddChecklistItem_Click(object sender, RoutedEventArgs e)
        => await RunSafelyAsync(() => ViewModel.Detail.AddChecklistItemCommand.ExecuteAsync(null));

    private void TagRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TagEditorOption option })
            ViewModel.Detail.ToggleTag(option.Id);
    }

    // The + 새 태그 affordance opens an inline field in the tag card rather than a modal: type a name
    // and press Enter (or 추가) to create + select it; Escape or blurring an empty field dismisses it.
    private void BeginAddTag_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Detail.BeginAddTag();
        DispatcherQueue.TryEnqueue(() => NewTagBox.Focus(FocusState.Programmatic));
    }

    private async void NewTagBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await CommitNewTagAsync();
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            ViewModel.Detail.CancelAddTag();
        }
    }

    private async void ConfirmAddTag_Click(object sender, RoutedEventArgs e)
        => await CommitNewTagAsync();

    private async void NewTagBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NewTagBox.Text))
            ViewModel.Detail.CancelAddTag();
        else
            await CommitNewTagAsync();
    }

    // Guarded so the LostFocus that fires when 추가/Enter moves focus can't double-create the tag.
    private bool _committingTag;

    private async Task CommitNewTagAsync()
    {
        if (_committingTag) return;
        _committingTag = true;
        try { await RunSafelyAsync(() => ViewModel.Detail.ConfirmAddTagCommand.ExecuteAsync(null)); }
        finally { _committingTag = false; }
    }

    private async void DeleteChecklistItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "체크리스트 항목을 삭제할까요?",
            Content = "이 항목을 목록에서 지웁니다.",
            PrimaryButtonText = "삭제",
            CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Close,
        };
        await RunSafelyAsync(async () =>
        {
            if (await _dialogs.ShowAsync(dialog) == ContentDialogResult.Primary)
                await ViewModel.Detail.DeleteChecklistItemCommand.ExecuteAsync(id);
        });
    }

    private async void DeleteTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor) return;
        await RunSafelyAsync(async () =>
        {
            if (await ConfirmPopover.ShowAsync(anchor, new ConfirmPopoverOptions { Message = "이 할 일을 삭제할까요?" }))
                await ViewModel.Detail.DeleteTaskCommand.ExecuteAsync(null);
        });
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
