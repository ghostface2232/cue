using System.Numerics;
using Cue.Services;
using Cue.Domain;
using Cue.Storage.Recurrence;
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

/// <summary>A horizontally scrolling 주간 타임라인 (weekly timeline) over the index-backed task date
/// projection: ISO-week columns, a card per task placed at its week, navigated by month.</summary>
public sealed partial class WeeklyTimelinePage : Page
{
    private const double DetailDefaultWidth = 460;
    private const double DetailMinWidth = 320;
    private const double DetailAbsoluteMinWidth = 260;
    private const double DetailMaxWidth = 680;
    private const double DetailPrimaryMinWidth = 340;
    private const double DetailCompactBreakpoint = 390;
    // Below this content width the detail panel switches from a side-by-side column to a full-width
    // overlay over the timeline (matching TaskListPage). The hysteresis band prevents edge chatter.
    private const double SideBySideMinWidth = DetailPrimaryMinWidth + DetailAbsoluteMinWidth + 16 + 8;
    private const double LayoutHysteresis = 24;

    // One recurrence pip's horizontal stride (pip width + inter-pip spacing); used to fill and scroll the
    // 반복 기록 strip onto the current cycle.
    private const double TimelinePipStride = 52 + 4;
    private const double TimelineScrollStep = TimelinePipStride * 3;

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
    private bool _detailResizable;
    // Guards the one-time wheel-handler registration (Loaded can fire more than once).
    private bool _wheelHandlerAttached;

    // Set when the detail panel opens a recurring task, so the recurrence strip scrolls to its live head
    // (다음 / 종료) pip once it lays out. Cleared after the first scroll so paging older cycles in doesn't snap.
    private bool _timelineScrollToEndPending;

    private readonly DialogService _dialogs;
    private readonly INavDataChangeNotifier _navNotifier;
    private readonly AppPreferences _preferences;
    public WeeklyTimelineViewModel ViewModel { get; }

    public WeeklyTimelinePage()
    {
        ViewModel = App.Services.GetRequiredService<WeeklyTimelineViewModel>();
        _dialogs = App.Services.GetRequiredService<DialogService>();
        _navNotifier = App.Services.GetRequiredService<INavDataChangeNotifier>();
        _preferences = App.Services.GetRequiredService<AppPreferences>();
        _detailPreferredWidth = _preferences.DetailPanelWidth ?? DetailDefaultWidth;
        InitializeComponent();
        // Reflect groups/tags created elsewhere (the sidebar, another panel) in this panel's option
        // lists at once. Unsubscribed on navigate-away (the Frame discards the page).
        _navNotifier.Changed += OnNavDataChanged;
        // The 반복 종료 / 삭제 action row reflows as the detail panel opens a (non-)recurring task.
        ViewModel.Detail.PropertyChanged += Detail_PropertyChanged;
    }

    private async void OnNavDataChanged(object? sender, EventArgs e)
        => await RunSafelyAsync(() => ViewModel.Detail.ReloadNavOptionsAsync());

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _navNotifier.Changed -= OnNavDataChanged;
        ViewModel.Detail.PropertyChanged -= Detail_PropertyChanged;
        if (ViewModel.Detail.IsOpen)
        {
            CommitFocusedTextBox();
            ObserveFlushTask(ViewModel.Detail.FlushAsync());
        }
        base.OnNavigatedFrom(e);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await RunSafelyAsync(async () =>
        {
            await ViewModel.LoadCommand.ExecuteAsync(null);
            CenterTodayInView(animate: false);
        });
    }

    private async void PreviousMonth_Click(object sender, RoutedEventArgs e)
        => await RunSafelyAsync(async () =>
        {
            await ViewModel.PreviousMonthCommand.ExecuteAsync(null);
            // Animate the band leftward to the earlier month — a directional slide over the continuous year.
            AnimateToFocusedMonth();
        });

    private async void Today_Click(object sender, RoutedEventArgs e)
        => await RunSafelyAsync(async () =>
        {
            await ViewModel.GoTodayCommand.ExecuteAsync(null);
            CenterTodayInView(animate: true);
        });

    private async void NextMonth_Click(object sender, RoutedEventArgs e)
        => await RunSafelyAsync(async () =>
        {
            await ViewModel.NextMonthCommand.ExecuteAsync(null);
            // Animate the band rightward to the later month — a directional slide over the continuous year.
            AnimateToFocusedMonth();
        });

    // --- Horizontal scroll / drag-pan / wheel-pan / keyboard ---

    private void TimelineScrollViewer_Loaded(object sender, RoutedEventArgs e)
    {
        // The ScrollViewer marks the wheel event handled internally before a XAML PointerWheelChanged would
        // run, so the wheel→horizontal pan handler is registered here with handledEventsToo:true. Guarded so
        // re-entering the page (a fresh Loaded) doesn't stack duplicate handlers.
        if (!_wheelHandlerAttached)
        {
            TimelineScrollViewer.AddHandler(
                UIElement.PointerWheelChangedEvent,
                new PointerEventHandler(TimelineScrollViewer_PointerWheelChanged),
                handledEventsToo: true);
            _wheelHandlerAttached = true;
        }
        ViewModel.SetViewportWidth(TimelineViewportWidth());
        FocusTimeline();
        CenterTodayInView(animate: false);
    }

    // The timeline content viewport width that drives the dynamic column width. Prefer the ScrollViewer's
    // own viewport; fall back to its actual width before it has measured.
    private double TimelineViewportWidth()
        => TimelineScrollViewer.ViewportWidth > 0 ? TimelineScrollViewer.ViewportWidth : TimelineScrollViewer.ActualWidth;

    // Feeds the timeline content width to the view model so the week columns stretch to fill a wide window
    // (no dead strip on the right) and stay at their minimum (scrolling) when narrow.
    private void TimelineScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        => ViewModel.SetViewportWidth(TimelineViewportWidth());

    /// <summary>Builds the left-positioning margin for a card from its computed left offset (the view-model
    /// layer keeps it a plain double since it doesn't reference WinUI's <see cref="Thickness"/>).</summary>
    public static Thickness LeftMargin(double left) => new(left, 6, 0, 6);

    private void TimelineScrollViewer_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var direction = e.Key switch
        {
            VirtualKey.Left => -1,
            VirtualKey.Right => 1,
            _ => 0,
        };
        if (direction == 0) return;

        e.Handled = true;
        ScrollByColumns(direction);
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
        // Wheel up → earlier weeks (scroll left); wheel down → later weeks. One column per notch.
        ScrollByColumns(delta > 0 ? -1 : 1);
    }

    private void EndPointerPan(PointerRoutedEventArgs e)
    {
        if (!_isPointerPanning)
            return;

        _isPointerPanning = false;
        TimelineScrollViewer.ReleasePointerCapture(e.Pointer);
        // Rest on a whole-column boundary so a free drag never leaves a half-clipped column at the right edge.
        if (_panMoved)
            TimelineScrollViewer.ChangeView(
                ClampOffset(SnapToColumn(TimelineScrollViewer.HorizontalOffset), TimelineScrollViewer.ScrollableWidth),
                null,
                null,
                disableAnimation: false);
        e.Handled = true;
    }

    // animate: false jumps straight to today (used on first open, so the year band doesn't visibly scroll
    // all the way from January); true slides there (the 오늘 button, a deliberate directional move).
    private void CenterTodayInView(bool animate)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            TimelineScrollViewer.UpdateLayout();
            FocusTimeline();
            if (!ViewModel.HasTodayInRange)
                return;

            // Land on a column boundary: the columns tile the viewport exactly, so snapping the offset to a
            // whole number of column widths keeps the visible columns flush on both edges (no half-clipped
            // column at the right). Centering today, then rounding to the nearest column, does both.
            var raw = ViewModel.TodayLineOffset - (TimelineScrollViewer.ViewportWidth / 2);
            TimelineScrollViewer.ChangeView(
                ClampOffset(SnapToColumn(raw), TimelineScrollViewer.ScrollableWidth),
                null,
                null,
                disableAnimation: !animate);
        });
    }

    // Animate the band to the focused month's first week column (a directional slide over the continuous
    // year). FocusedMonthOffset is column-aligned. Deferred so the offset reflects any just-rebuilt year.
    private void AnimateToFocusedMonth() => ScrollHorizontally(ViewModel.FocusedMonthOffset);

    private void ScrollHorizontally(double offset)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            TimelineScrollViewer.UpdateLayout();
            FocusTimeline();
            TimelineScrollViewer.ChangeView(
                ClampOffset(offset, TimelineScrollViewer.ScrollableWidth),
                null,
                null,
                disableAnimation: false);
        });
    }

    private void FocusTimeline()
        => TimelineScrollViewer.Focus(FocusState.Programmatic);

    // Keep the month heading in step with what's on screen: once a scroll settles (nav animation, wheel,
    // drag), resolve the focused month from the rested offset. Intermediate frames are skipped so the title
    // doesn't flicker mid-slide.
    private void TimelineScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate)
            return;
        ViewModel.SyncFocusedMonthToOffset(TimelineScrollViewer.HorizontalOffset);
    }

    // The columns tile the viewport exactly (see ComputeWeekWidth), so all horizontal movement lands on a
    // whole-column boundary: at rest the view always shows N full columns flush on both edges, never a
    // half-clipped column at the right. Wheel and arrow keys step a column at a time; a free drag snaps to
    // the nearest column on release.
    private void ScrollByColumns(int columns)
    {
        var weekWidth = ViewModel.WeekWidth;
        if (weekWidth <= 0)
            return;
        var target = SnapToColumn(TimelineScrollViewer.HorizontalOffset) + (columns * weekWidth);
        TimelineScrollViewer.ChangeView(
            ClampOffset(target, TimelineScrollViewer.ScrollableWidth),
            null,
            null,
            disableAnimation: false);
    }

    private double SnapToColumn(double offset)
    {
        var weekWidth = ViewModel.WeekWidth;
        return weekWidth > 0 ? Math.Round(offset / weekWidth) * weekWidth : offset;
    }

    private static double ClampOffset(double value, double maximum)
        => Math.Min(Math.Max(0, value), Math.Max(0, maximum));

    // --- Cards: open detail, hover lift, entrance animation ---

    private async void TimelineBar_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_panMoved)
        {
            _panMoved = false;
            return;
        }

        // Tapping the card's checkbox (or any control) just toggles/acts — it must not open the detail.
        if (sender is not FrameworkElement { Tag: Guid id } element || IsInteractiveElement(e.OriginalSource as DependencyObject))
            return;

        e.Handled = true;
        // Give the card keyboard focus so a follow-up Delete acts on it (matching the list view).
        element.Focus(FocusState.Pointer);
        await SelectTaskAndCenterTimelineAsync(id);
    }

    // Delete on a focused card soft-deletes that task (with the same anchored confirmation as the list).
    private async void TimelineCard_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Delete || sender is not FrameworkElement { Tag: Guid id } element)
            return;
        e.Handled = true;
        await RunSafelyAsync(async () =>
        {
            if (await ConfirmDeleteTaskAsync(element))
                await ViewModel.DeleteTaskCommand.ExecuteAsync(id);
        });
    }

    // Right-click a card opens its context menu: move to a group, toggle tags, rename, end series, delete —
    // the same actions as a list row.
    private async void TimelineBar_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid id } element)
            return;
        e.Handled = true;
        element.Focus(FocusState.Pointer);
        await RunSafelyAsync(async () =>
        {
            var menu = await BuildTaskContextMenuAsync(id, element);
            menu.ShowAt(element, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
            {
                Position = e.GetPosition(element),
            });
        });
    }

    private async Task<MenuFlyout> BuildTaskContextMenuAsync(Guid id, FrameworkElement anchor)
    {
        var task = await ViewModel.GetTaskAsync(id);
        var taskGroups = await ViewModel.GetTaskGroupsAsync();
        var tags = await ViewModel.GetTagsAsync();
        var menu = new MenuFlyout();

        // 그룹으로 이동 — every group, plus a "그룹에서 빼기" entry that returns the task to the Cue home.
        var moveGroup = new MenuFlyoutSubItem { Text = "그룹으로 이동" };
        var clearGroup = new MenuFlyoutItem { Text = "그룹에서 빼기" };
        if (task?.TaskGroupId is null) clearGroup.Icon = CheckIcon();
        clearGroup.Click += async (_, _) => await RunSafelyAsync(() => ViewModel.MoveTaskToTaskGroupAsync(id, null));
        moveGroup.Items.Add(clearGroup);
        if (taskGroups.Count > 0) moveGroup.Items.Add(new MenuFlyoutSeparator());
        foreach (var taskGroup in taskGroups)
        {
            var taskGroupId = taskGroup.Id;
            var item = new MenuFlyoutItem { Text = taskGroup.Name };
            if (task?.TaskGroupId == taskGroupId) item.Icon = CheckIcon();
            item.Click += async (_, _) => await RunSafelyAsync(() => ViewModel.MoveTaskToTaskGroupAsync(id, taskGroupId));
            moveGroup.Items.Add(item);
        }
        menu.Items.Add(moveGroup);

        // 태그 — a multi-select list; each toggles independently so a task can carry several at once.
        var tagGroup = new MenuFlyoutSubItem { Text = "태그" };
        if (tags.Count == 0)
        {
            tagGroup.Items.Add(new MenuFlyoutItem { Text = "태그 없음", IsEnabled = false });
        }
        else
        {
            foreach (var tag in tags)
            {
                var tagId = tag.Id;
                var item = new ToggleMenuFlyoutItem
                {
                    Text = tag.Name,
                    IsChecked = task?.TagIds.Contains(tagId) == true,
                };
                item.Click += async (_, _) => await RunSafelyAsync(() => ViewModel.ToggleTaskTagAsync(id, tagId));
                tagGroup.Items.Add(item);
            }

            // 태그 지우기 — clears outright with one tag; with two or more it expands into a picker (plus a
            // 모두 지우기 escape hatch) so a single tag can be dropped without losing the rest.
            tagGroup.Items.Add(new MenuFlyoutSeparator());
            var assigned = task?.TagIds ?? (IReadOnlyList<Guid>)Array.Empty<Guid>();
            if (assigned.Count >= 2)
            {
                var clearTags = new MenuFlyoutSubItem { Text = "태그 지우기" };
                var clearAll = new MenuFlyoutItem { Text = "모두 지우기" };
                clearAll.Click += async (_, _) => await RunSafelyAsync(() => ViewModel.ClearTaskTagsAsync(id));
                clearTags.Items.Add(clearAll);
                clearTags.Items.Add(new MenuFlyoutSeparator());
                foreach (var tag in tags)
                {
                    if (!assigned.Contains(tag.Id)) continue;
                    var tagId = tag.Id;
                    var remove = new MenuFlyoutItem { Text = tag.Name };
                    remove.Click += async (_, _) => await RunSafelyAsync(() => ViewModel.RemoveTaskTagAsync(id, tagId));
                    clearTags.Items.Add(remove);
                }
                tagGroup.Items.Add(clearTags);
            }
            else
            {
                var clearTags = new MenuFlyoutItem { Text = "태그 지우기", IsEnabled = assigned.Count > 0 };
                clearTags.Click += async (_, _) => await RunSafelyAsync(() => ViewModel.ClearTaskTagsAsync(id));
                tagGroup.Items.Add(clearTags);
            }
        }
        menu.Items.Add(tagGroup);

        var rename = new MenuFlyoutItem { Text = "이름 바꾸기" };
        rename.Click += async (_, _) => await RunSafelyAsync(async () =>
        {
            var name = await PromptNameAsync("할 일 이름 바꾸기", "할 일 제목", task?.Title ?? string.Empty);
            if (name is not null) await ViewModel.RenameTaskAsync(id, name);
        });
        menu.Items.Add(rename);

        // 반복 종료 — only for a recurring task; ends the series (to 완료한 일), distinct from 삭제.
        if (task?.Recurrence is not null)
        {
            var endSeries = new MenuFlyoutItem { Text = "반복 종료" };
            endSeries.Click += async (_, _) => await RunSafelyAsync(async () =>
            {
                if (await ConfirmEndSeriesAsync(anchor))
                    await ViewModel.EndSeriesAsync(id);
            });
            menu.Items.Add(endSeries);
        }

        menu.Items.Add(new MenuFlyoutSeparator());

        var delete = new MenuFlyoutItem { Text = "삭제" };
        if (Application.Current.Resources["SystemFillColorCriticalBrush"] is Microsoft.UI.Xaml.Media.Brush critical)
            delete.Foreground = critical;
        delete.Click += async (_, _) => await RunSafelyAsync(async () =>
        {
            if (await ConfirmDeleteTaskAsync(anchor))
                await ViewModel.DeleteTaskCommand.ExecuteAsync(id);
        });
        menu.Items.Add(delete);

        return menu;
    }

    private static FontIcon CheckIcon() => new() { Glyph = "", FontSize = 14 };

    private Task<bool> ConfirmDeleteTaskAsync(FrameworkElement anchor)
        => ConfirmPopover.ShowAsync(anchor, new ConfirmPopoverOptions { Message = "이 할 일을 삭제할까요?" });

    private async Task<string?> PromptNameAsync(string title, string placeholder, string initial = "")
    {
        var input = new TextBox { Text = initial, PlaceholderText = placeholder, MinWidth = 320 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = input,
            PrimaryButtonText = "저장",
            CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await _dialogs.ShowAsync(dialog);
        var name = input.Text.Trim();
        return result == ContentDialogResult.Primary && name.Length > 0 ? name : null;
    }

    // Double-clicking empty space creates a task in the week column under the pointer and opens it. A card
    // consumes its own double-tap (TimelineBar_DoubleTapped) so only genuinely empty space reaches here.
    private async void TimelineSurface_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement track)
            return;
        var weekWidth = ViewModel.WeekWidth;
        if (weekWidth <= 0)
            return;

        var x = e.GetPosition(track).X;
        var weekIndex = (int)(x / weekWidth);
        e.Handled = true;
        await RunSafelyAsync(() => ViewModel.CreateTaskInWeekCommand.ExecuteAsync(weekIndex));
        // Opening the new task's panel narrows the timeline; keep its (clicked) week column pinned left.
        ScrollColumnToLeft(() => weekIndex * ViewModel.WeekWidth);
    }

    // A double-tap on a card is consumed so it doesn't bubble to the surface and create a task; the card's
    // single tap already opens it.
    private void TimelineBar_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        => e.Handled = true;

    /// <summary>Selects a task and, when this swaps the content of an already-open panel, re-centers the
    /// recurrence strip on the current cycle once the new content lays out. For a fresh open the panel's
    /// Visibility callback already arms the same scroll.</summary>
    private async Task SelectTaskAndCenterTimelineAsync(Guid id)
    {
        var switching = ViewModel.Detail.IsOpen && ViewModel.Detail.CurrentTaskId != id;
        if (switching) _timelineScrollToEndPending = true;
        await RunSafelyAsync(() => ViewModel.SelectTaskCommand.ExecuteAsync(id));
        // Pin the tapped card's week column to the left edge: opening the panel narrows the timeline and
        // recomputes the column width, which would otherwise leave the scroll on a different week.
        ScrollColumnToLeft(() => ViewModel.ColumnOffsetForTask(id));
        if (switching)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, RefreshTimelineViewport);
    }

    // Pins a week column to the left edge after the detail panel's open/resize settles (so the recomputed
    // column width is in effect). Deferred + UpdateLayout so the offset is read post-resize; placed without
    // animation so it lands directly on the column rather than visibly sliding from the bumped position.
    private void ScrollColumnToLeft(Func<double> offset)
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            TimelineScrollViewer.UpdateLayout();
            var x = offset();
            if (x < 0)
                return;
            TimelineScrollViewer.ChangeView(
                ClampOffset(SnapToColumn(x), TimelineScrollViewer.ScrollableWidth),
                null,
                null,
                disableAnimation: true);
        });
    }

    private static bool IsInteractiveElement(DependencyObject? element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Button or CheckBox)
                return true;
        }
        return false;
    }

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

    // --- Detail panel: open/close animation, responsive split, resize handle ---

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
            // A freshly opened panel should land the recurrence strip on its live head pip; the strip's
            // SizeChanged does the actual scroll once it has laid out (no-op for a non-recurring task).
            if (shown) _timelineScrollToEndPending = true;
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
            SetDetailResizeGripVisible(false);
        }
        else
        {
            Grid.SetColumn(DetailPanel, 1);
            Grid.SetColumnSpan(DetailPanel, 1);
        }
        UpdateContentObscured();
        ApplyDetailPanelWidth();   // owns the resize-handle visibility (hidden when not resizable)
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
        if (!CanResizeDetailNow()) return;   // pinned at its limit, or overlay — nothing to drag
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
        // Persist the clamped, applied width so the resize sticks across lists and launches.
        if (!_detailOverlay && DetailPanel.Width > 0)
            _preferences.DetailPanelWidth = DetailPanel.Width;
        e.Handled = true;
    }

    private void DetailResizeHandle_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (CanResizeDetailNow())
            SetDetailResizeGripVisible(true);
    }

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
        if (_detailOverlay)
        {
            _detailResizable = false;
            SetDetailResizeGripVisible(false);
            UpdateDetailResponsiveLayout();
            return;
        }

        if (ContentSplitGrid.ActualWidth <= 0)
            return;

        var (min, max) = DetailWidthRange();
        var width = Math.Clamp(_detailPreferredWidth, min, max);
        if (double.IsNaN(DetailPanel.Width) || Math.Abs(DetailPanel.Width - width) > 0.5)
            DetailPanel.Width = width;

        _detailResizable = max - min > 4;
        if (!_detailResizable)
            SetDetailResizeGripVisible(false);

        UpdateDetailResponsiveLayout();
    }

    private bool CanResizeDetailNow()
    {
        if (_detailOverlay) return false;
        if (ContentSplitGrid.ActualWidth <= 0) return false;
        var (min, max) = DetailWidthRange();
        return max - min > 4;
    }

    private (double Min, double Max) DetailWidthRange()
    {
        var maxByWindow = ContentSplitGrid.ActualWidth - DetailPrimaryMinWidth - ContentSplitGrid.ColumnSpacing - 8;
        var max = Math.Min(DetailMaxWidth, Math.Max(DetailAbsoluteMinWidth, maxByWindow));
        var min = Math.Min(DetailMinWidth, max);
        return (min, max);
    }

    // 반복 종료 + 삭제 share one row. A recurring task gives the left column to 반복 종료 and seats 삭제 in
    // the right column; otherwise the left column collapses and 삭제 spans both columns.
    private void UpdateRecurringActionsLayout()
    {
        var recurring = ViewModel.Detail.IsRecurring;
        RecurringActionsGrid.ColumnDefinitions[0].Width =
            recurring ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(DeleteTaskButton, recurring ? 1 : 0);
        Grid.SetColumnSpan(DeleteTaskButton, recurring ? 1 : 2);
    }

    private void UpdateDetailResponsiveLayout()
    {
        var width = DetailPanel.ActualWidth > 0 ? DetailPanel.ActualWidth : DetailPanel.Width;
        var compact = width < DetailCompactBreakpoint;
        SetResponsivePair(DetailMetaGrid, DetailGroupField, compact, new GridLength(1, GridUnitType.Star));
        SetResponsivePair(WhenDateTimeGrid, WhenTimePanel, compact, GridLength.Auto);

        var star = new GridLength(1, GridUnitType.Star);
        WhenHourCol.Width = compact ? star : GridLength.Auto;
        WhenMinuteCol.Width = compact ? star : GridLength.Auto;
        WhenHourCombo.Width = compact ? double.NaN : 72;
        WhenMinuteCombo.Width = compact ? double.NaN : 72;
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

    // --- Recurrence detail: 반복 기록 strip + series lifecycle ---

    private Task<bool> ConfirmEndSeriesAsync(FrameworkElement anchor)
        => ConfirmPopover.ShowAsync(anchor, new ConfirmPopoverOptions
        {
            Message = "반복을 종료하고 완료한 일로 옮길까요?",
            ConfirmText = "반복 종료",
            Destructive = false,
        });

    private async void EndSeries_Click(object sender, RoutedEventArgs e)
        => await RunSafelyAsync(async () =>
        {
            if (sender is FrameworkElement anchor && await ConfirmEndSeriesAsync(anchor))
                await ViewModel.Detail.EndSeriesCommand.ExecuteAsync(null);
        });

    /// <summary>‹ — pages older recorded cycles in if any remain, otherwise nudges the strip back.</summary>
    private void TimelinePrev_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Detail.HasOlderTimeline)
            _ = RunSafelyAsync(() => ViewModel.Detail.LoadOlderTimelineCommand.ExecuteAsync(null));
        else
            ScrollTimeline(-1);
    }

    /// <summary>› — nudges the strip back toward the live head (다음 / 종료) pip.</summary>
    private void TimelineNext_Click(object sender, RoutedEventArgs e) => ScrollTimeline(1);

    private void ScrollTimeline(int direction)
    {
        if (TimelineScroller is not { } scroller) return;
        var target = scroller.HorizontalOffset + direction * TimelineScrollStep;
        scroller.ChangeView(target, null, null, disableAnimation: !_animationsEnabled);
    }

    private void TimelineRepeater_SizeChanged(object sender, SizeChangedEventArgs e)
        => RefreshTimelineViewport();

    private void RefreshTimelineViewport()
    {
        if (TimelineScroller is not { } scroller) return;

        if (scroller.ViewportWidth > 0)
            ViewModel.Detail.SetVisibleFutureCount((int)Math.Ceiling(scroller.ViewportWidth / TimelinePipStride));

        if (!_timelineScrollToEndPending || scroller.ScrollableWidth <= 0) return;
        _timelineScrollToEndPending = false;
        var target = Math.Max(0, (ViewModel.Detail.CurrentCycleIndex - 2) * TimelinePipStride);
        scroller.ChangeView(target, null, null, disableAnimation: true);
    }

    private async void OccurrencePip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: OccurrencePipViewModel pip } anchor) return;
        if (pip.OccurrenceId is not { } occurrenceId) return; // current / future / 종료 — not a record

        await RunSafelyAsync(async () =>
        {
            if (pip is { Kind: OccurrencePipKind.Completed, IsLatestRecord: true }
                && await ViewModel.Detail.UndoCompletionAsync(occurrenceId))
                return;

            var occurrence = await ViewModel.Detail.GetOccurrenceAsync(occurrenceId);
            if (occurrence is null) return;
            BuildOccurrenceFlyout(pip, occurrence).ShowAt(anchor);
        });
    }

    private Flyout BuildOccurrenceFlyout(OccurrencePipViewModel pip, RecurrenceOccurrence occurrence)
    {
        var flyout = new Flyout();
        var content = new StackPanel { Spacing = 10, MinWidth = 200, MaxWidth = 280 };
        var bodyFont = (FontFamily)Application.Current.Resources["CueFontFamily"];

        content.Children.Add(new TextBlock
        {
            Text = pip.Tooltip,
            FontFamily = (FontFamily)Application.Current.Resources["CueFontFamilyMedium"],
            TextWrapping = TextWrapping.Wrap,
        });

        if (occurrence.ChecklistSnapshot.Count > 0)
        {
            var list = new StackPanel { Spacing = 4 };
            foreach (var item in occurrence.ChecklistSnapshot)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(new FontIcon
                {
                    FontSize = 13,
                    Glyph = item.IsChecked ? "" : "",
                    Foreground = (Brush)Application.Current.Resources[item.IsChecked ? "CueTimelineCompletedBrush" : "CueTimelineMutedBrush"],
                });
                row.Children.Add(new TextBlock
                {
                    Text = item.Title,
                    FontFamily = bodyFont,
                    FontSize = 13,
                    Opacity = item.IsChecked ? 0.55 : 1.0,
                    TextWrapping = TextWrapping.Wrap,
                });
                list.Children.Add(row);
            }
            content.Children.Add(list);
        }

        content.Children.Add(new TextBlock
        {
            Text = "상태 변경",
            FontFamily = bodyFont,
            FontSize = 12,
            Opacity = 0.6,
        });
        var picker = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (status, label) in new[]
        {
            (OccurrenceStatus.Completed, "완료"),
            (OccurrenceStatus.Missed, "미수행"),
        })
        {
            var captured = status;
            var button = new Button { Content = label, MinWidth = 56 };
            if (occurrence.Status == status
                && Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accentStyle)
                && accentStyle is Style accent)
                button.Style = accent;
            button.Click += async (_, _) =>
            {
                flyout.Hide();
                await RunSafelyAsync(() => ViewModel.Detail.UpdateOccurrenceStatusAsync(occurrence.Id, captured));
            };
            picker.Children.Add(button);
        }
        content.Children.Add(picker);

        flyout.Content = content;
        return flyout;
    }

    // --- Detail panel: 일시 / 제목 / 메모 / 태그 / 체크리스트 / 삭제 ---

    private void EnableWhen_Click(object sender, RoutedEventArgs e) => ViewModel.Detail.EnableWhenEditor();
    private void ClearWhen_Click(object sender, RoutedEventArgs e) => ViewModel.Detail.ClearWhen();

    private async void CloseDetail_Click(object sender, RoutedEventArgs e)
        => await RunSafelyAsync(CloseDetailWithAnimationAsync);

    private async Task CloseDetailWithAnimationAsync()
    {
        // Commit the focused editor, then observe the flush without allowing an exhausted save retry to
        // escape an async-void UI event. The app-scoped failure coordinator keeps the unsaved work visible
        // and retryable even after this panel closes.
        CommitFocusedTextBox();
        ObserveFlushTask(ViewModel.Detail.FlushAsync());

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
    private void DetailText_LostFocus(object sender, RoutedEventArgs e)
        => ObserveFlushTask(ViewModel.Detail.FlushAsync());

    // The title commits on Enter by blurring onto the scroll host (its LostFocus then flushes the save).
    private void DetailTitle_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        ReleaseDetailInputFocus();
    }

    // A checklist item commits on Enter by pushing the box's current text to its row view model (the Text
    // binding otherwise updates only on focus-out), then blurring so the edit reads as done.
    private void ChecklistItemTitle_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || sender is not TextBox { DataContext: ChecklistItemViewModel item } box) return;
        e.Handled = true;
        item.Title = box.Text;
        ReleaseDetailInputFocus();
    }

    private async void NewChecklistItem_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        ReleaseDetailInputFocus();
        await RunSafelyAsync(() => ViewModel.Detail.AddChecklistItemCommand.ExecuteAsync(null));
    }

    private void ReleaseDetailInputFocus() => DetailScrollViewer.Focus(FocusState.Programmatic);

    private async void AddChecklistItem_Click(object sender, RoutedEventArgs e)
        => await RunSafelyAsync(() => ViewModel.Detail.AddChecklistItemCommand.ExecuteAsync(null));

    private void TagRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TagEditorOption option })
            ViewModel.Detail.ToggleTag(option.Id);
    }

    // The + 새 태그 affordance opens an inline field in the tag card: type a name and press Enter (or 추가)
    // to create + select it; Escape or blurring an empty field dismisses it.
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

    // --- Tag-row name fade (the tag card's overflow gradient) ---

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
        if (FindDescendant<Rectangle>(parent, "TagNameFade") is not { } fade)
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

    private void Detail_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TaskDetailViewModel.IsRecurring) or nameof(TaskDetailViewModel.IsOpen))
            UpdateRecurringActionsLayout();
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

    public bool IsDetailSaving => ViewModel?.Detail?.IsSaving ?? false;
    public Task FlushDetailAsync() => ViewModel?.Detail?.FlushAsync() ?? Task.CompletedTask;
    public TaskDetailViewModel? DetailViewModel => ViewModel?.Detail;

    public void CommitFocusedTextBox()
    {
        if (XamlRoot is null) return;
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox focusedTextBox)
            focusedTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    private static void ObserveFlushTask(Task task)
    {
        _ = task.ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception is { } exception)
                _ = exception.Flatten();
        }, TaskContinuationOptions.OnlyOnFaulted);
    }
}
