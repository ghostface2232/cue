using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Cue.ViewModels;
using Cue.Services;
using Cue.Domain;
using Windows.System;
using Windows.UI.ViewManagement;

namespace Cue.Pages;

/// <summary>
/// Hosts one index-backed task list: the quick-add line and the list below. The view model is
/// resolved from DI; the navigation parameter selects which index view it reflects.
/// </summary>
public sealed partial class TaskListPage : Page
{
    private const double DetailDefaultWidth = 460;
    private const double DetailMinWidth = 320;
    private const double DetailAbsoluteMinWidth = 260;
    private const double DetailMaxWidth = 680;
    private const double DetailPrimaryMinWidth = 340;
    private const double DetailCompactBreakpoint = 390;

    // Below this content width the list + detail panel can't sit side by side comfortably, so the panel
    // switches to a full-width overlay over the list. Set near the floor where the list can still hold
    // its primary min beside a shrunk panel, so the side-by-side (and its resize handle) survives as far
    // down as it usefully can. A small hysteresis band keeps the switch from chattering at the edge.
    private const double SideBySideMinWidth = 600;
    private const double LayoutHysteresis = 24;
    // The list reflows each row's right-edge group/tag chips beneath the title once its column gets this
    // narrow — independent of the panel, since the list also narrows when the panel is open side by side.
    private const double ListCompactWidth = 500;

    public TaskListViewModel ViewModel { get; }
    private readonly DialogService _dialogs;
    private readonly INavDataChangeNotifier _navNotifier;
    private readonly AppPreferences _preferences;
    private readonly bool _animationsEnabled = new UISettings().AnimationsEnabled;
    private readonly ConditionalWeakTable<ItemsRepeater, ReorderSurface> _reorderSurfaces = new();
    // Task-row repeaters (the open list + each priority section), used to locate a row's realized
    // container for the post-completion fold/spin. Completed/Logbook repeaters are not registered — their
    // rows can't enter the acknowledgement flow.
    private readonly List<ItemsRepeater> _taskRepeaters = new();
    // Per-row pending fold timers for the completion-acknowledgement moment, so a hover can pause one and
    // an undo/finalize can cancel it.
    private readonly Dictionary<TaskRowViewModel, Microsoft.UI.Dispatching.DispatcherQueueTimer> _ackTimers = new();
    private Visual? _detailPanelVisual;
    private bool _isResizingDetail;
    private double _detailPreferredWidth;
    private double _resizeStartX;
    private double _resizeStartWidth;
    private bool _detailOverlay;
    private bool _listCompact;
    private bool _detailResizable;

    // Set while a drag-reorder commits, so the row that moves in the bound collection does not also
    // play the list's entrance animation on top of the drop settle.
    private bool _suppressItemEntrance;

    // Set when the detail panel opens a recurring task, so the recurrence timeline scrolls to its live
    // head (next / 종료) pip once the strip lays out — showing the recent cycles and the next by default.
    // Cleared after the first scroll so paging older cycles in doesn't snap back to the head.
    private bool _timelineScrollToEndPending;

    public TaskListPage()
    {
        ViewModel = App.Services.GetRequiredService<TaskListViewModel>();
        _dialogs = App.Services.GetRequiredService<DialogService>();
        _navNotifier = App.Services.GetRequiredService<INavDataChangeNotifier>();
        _preferences = App.Services.GetRequiredService<AppPreferences>();
        // Seed from the app-scoped preference so a width the user dragged on another list carries over;
        // the resize range still clamps it per window size in ApplyDetailPanelWidth.
        _detailPreferredWidth = _preferences.DetailPanelWidth ?? DetailDefaultWidth;
        InitializeComponent();
        // Reflect groups/tags created elsewhere (the sidebar, another panel) in this panel's option
        // lists at once. Unsubscribed on navigate-away (the Frame discards the page).
        _navNotifier.Changed += OnNavDataChanged;
        // The view model raises this right after a task is completed from an active list; the page runs
        // the in-row acknowledgement timing (fold, and a refresh spin for a repeating task).
        ViewModel.CompletionAcknowledged += OnCompletionAcknowledged;
    }

    private async void OnNavDataChanged(object? sender, EventArgs e)
        => await RunSafelyAsync(() => ViewModel.Detail.ReloadNavOptionsAsync());

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _navNotifier.Changed -= OnNavDataChanged;
        ViewModel.CompletionAcknowledged -= OnCompletionAcknowledged;
        foreach (var timer in _ackTimers.Values) timer.Stop();
        _ackTimers.Clear();
        base.OnNavigatedFrom(e);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        await RunSafelyAsync(async () =>
        {
            base.OnNavigatedTo(e);
            var navigation = e.Parameter as TaskListNavigation;
            if (navigation is null)
            {
                var mode = Enum.TryParse<TaskListMode>(e.Parameter as string, ignoreCase: true, out var parsed)
                    ? parsed
                    : TaskListMode.AllTasks;
                navigation = new TaskListNavigation(mode);
            }
            ViewModel.SetNavigation(navigation);
            await ViewModel.LoadCommand.ExecuteAsync(null);
        });
    }

    private async void QuickAdd_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;
        e.Handled = true;
        if (ViewModel.AddCommand.CanExecute(null))
            await RunSafelyAsync(() => ViewModel.AddCommand.ExecuteAsync(null));
    }

    private async void TaskSurface_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid id } element || IsInteractiveElement(e.OriginalSource as DependencyObject))
            return;
        // While the row is showing its completion acknowledgement, a tap shouldn't open the (now
        // completed) task — it only keeps the row alive (handled by the hover pause).
        if (element.DataContext is TaskRowViewModel { IsAcknowledging: true })
        {
            e.Handled = true;
            return;
        }
        e.Handled = true;
        // Give the row keyboard focus too, so a follow-up Delete acts on the just-selected task.
        element.Focus(FocusState.Pointer);
        await RunSafelyAsync(() => ViewModel.SelectTaskCommand.ExecuteAsync(id));
    }

    /// <summary>Tapping a nested checklist row opens its parent task's detail (its <c>Tag</c> is the
    /// parent id). The checkbox still just toggles — IsInteractiveElement guards that. A checklist item
    /// isn't a task, so this row never grabs Delete-focus the way a task row does.</summary>
    private async void ChecklistSurface_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid parentId } || IsInteractiveElement(e.OriginalSource as DependencyObject))
            return;
        e.Handled = true;
        await RunSafelyAsync(() => ViewModel.SelectTaskCommand.ExecuteAsync(parentId));
    }

    /// <summary>Delete on a focused row soft-deletes that task (with confirmation).</summary>
    private async void TaskSurface_KeyDown(object sender, KeyRoutedEventArgs e)
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

    /// <summary>Right-click on a row opens its context menu: move to a group, toggle tags, rename, delete.</summary>
    private async void TaskSurface_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid id } element)
            return;
        e.Handled = true;
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

        // 그룹으로 이동 — every group, plus a "no group" entry that returns the task to the Cue home.
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

        // 태그 — a checkable list; each click toggles one tag on the task.
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
        }
        menu.Items.Add(tagGroup);

        var rename = new MenuFlyoutItem { Text = "이름 바꾸기" };
        rename.Click += async (_, _) => await RunSafelyAsync(async () =>
        {
            var name = await PromptNameAsync("할 일 이름 바꾸기", "할 일 제목", task?.Title ?? string.Empty);
            if (name is not null) await ViewModel.RenameTaskAsync(id, name);
        });
        menu.Items.Add(rename);

        // 반복 종료 — only for a recurring task. Ends the series (completes it to 완료한 일); distinct from
        // 삭제, and from completing the current cycle (the row's checkbox).
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

    /// <summary>Anchored delete confirmation (a light popover, not a centered dialog). The file is
    /// kept; only the deletion time is recorded along with any checklist — so a one-line confirm,
    /// anchored to the row/button that triggered it, is enough.</summary>
    private Task<bool> ConfirmDeleteTaskAsync(FrameworkElement anchor)
        => ConfirmPopover.ShowAsync(anchor, new ConfirmPopoverOptions { Message = "이 할 일을 삭제할까요?" });

    private async void DeleteTask_Click(object sender, RoutedEventArgs e)
        => await RunSafelyAsync(async () =>
        {
            if (sender is FrameworkElement anchor && await ConfirmDeleteTaskAsync(anchor))
                await ViewModel.Detail.DeleteTaskCommand.ExecuteAsync(null);
        });

    // --- Recurrence detail panel: timeline + series lifecycle ---

    // How far the › / ‹ chevrons nudge the timeline strip: a few pips (pip width + spacing).
    private const double TimelineScrollStep = (52 + 4) * 3;

    /// <summary>Confirms 반복 종료 with a light anchored popover. Not destructive (nothing is deleted) —
    /// the series is completed and moves to 완료한 일 — so it uses a plain confirm, not the red delete tone.</summary>
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

    /// <summary>‹ — pages older recorded cycles in if any remain (history loads on demand, never all at
    /// once), otherwise just nudges the strip toward the older end.</summary>
    private void TimelinePrev_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Detail.HasOlderTimeline)
            _ = RunSafelyAsync(() => ViewModel.Detail.LoadOlderTimelineCommand.ExecuteAsync(null));
        else
            ScrollTimeline(-1);
    }

    /// <summary>› — nudges the strip back toward the live head (next / 종료) pip.</summary>
    private void TimelineNext_Click(object sender, RoutedEventArgs e) => ScrollTimeline(1);

    private void ScrollTimeline(int direction)
    {
        if (TimelineScroller is not { } scroller) return;
        var target = scroller.HorizontalOffset + direction * TimelineScrollStep;
        // With animations off the strip jumps rather than glides — the same information, no motion.
        scroller.ChangeView(target, null, null, disableAnimation: !_animationsEnabled);
    }

    /// <summary>When the strip's content finishes laying out after an open, scroll it to the live head pip
    /// on the right so the most recent cycles and the next/terminal cycle read by default. Cleared after
    /// the first scroll so paging older cycles in (‹) doesn't snap back to the head.</summary>
    private void TimelineRepeater_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_timelineScrollToEndPending || TimelineScroller is not { } scroller) return;
        if (scroller.ScrollableWidth <= 0) return;
        _timelineScrollToEndPending = false;
        scroller.ChangeView(scroller.ScrollableWidth, null, null, disableAnimation: true);
    }

    /// <summary>Opens a recorded cycle's flyout: its completion time, its frozen checklist snapshot, and
    /// the controls to correct its status. The live head pip (no occurrence id) is not editable, so it
    /// opens nothing. Editing a past cycle never shifts the series' next scheduled cycle.</summary>
    private async void OccurrencePip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: OccurrencePipViewModel pip } anchor) return;
        if (pip.OccurrenceId is not { } occurrenceId) return; // head pip — not a record

        await RunSafelyAsync(async () =>
        {
            var occurrence = await ViewModel.Detail.GetOccurrenceAsync(occurrenceId);
            if (occurrence is null) return;
            BuildOccurrenceFlyout(pip, occurrence).ShowAt(anchor);
        });
    }

    private Flyout BuildOccurrenceFlyout(OccurrencePipViewModel pip, RecurrenceOccurrence occurrence)
    {
        var flyout = new Flyout();
        var content = new StackPanel { Spacing = 10, MinWidth = 200, MaxWidth = 280 };

        // The cycle's date, status, and (for a completed cycle) its completion time — the pip's own tooltip
        // text already reads exactly this.
        content.Children.Add(new TextBlock
        {
            Text = pip.Tooltip,
            FontFamily = (FontFamily)Application.Current.Resources["CueFontFamilyMedium"],
            TextWrapping = TextWrapping.Wrap,
        });

        // The checklist exactly as it was ticked when the cycle was completed — read-only history.
        if (occurrence.ChecklistSnapshot.Count > 0)
        {
            var list = new StackPanel { Spacing = 4 };
            foreach (var item in occurrence.ChecklistSnapshot)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(new FontIcon
                {
                    FontSize = 13,
                    Glyph = item.IsChecked ? "" : "", // CheckMark / unchecked box
                    Foreground = (Brush)Application.Current.Resources[item.IsChecked ? "CueTimelineCompletedBrush" : "CueTimelineMutedBrush"],
                });
                row.Children.Add(new TextBlock
                {
                    Text = item.Title,
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
            FontSize = 12,
            Opacity = 0.6,
        });
        // The three status choices. Picking one corrects this cycle and closes the popover; it never
        // touches the series' next scheduled cycle (see UpdateOccurrenceStatusAsync).
        var picker = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (status, label) in new[]
        {
            (OccurrenceStatus.Completed, "완료"),
            (OccurrenceStatus.Skipped, "건너뜀"),
            (OccurrenceStatus.Missed, "미수행"),
        })
        {
            var captured = status;
            var button = new Button { Content = label, MinWidth = 56 };
            // Mark the current status so the picker reads as a choice, not three equal actions.
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

    private void TaskSurface_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        ConfigureImplicitAnimations(element);
        UpdateCenterPoint(element);
        element.SizeChanged += (_, _) => UpdateCenterPoint(element);
    }

    private void TaskSurface_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border border) return;
        border.Background = (Microsoft.UI.Xaml.Media.Brush)Resources["TaskHoverBrush"];
        // Hovering a row that's mid-acknowledgement holds it open: pause its pending fold (a one-off, where
        // the undo lives). A repeating completion rolls on regardless, so it isn't paused.
        if (border.DataContext is TaskRowViewModel { IsAcknowledging: true, IsRecurringCompletion: false } row)
            StopAckTimer(row);
    }

    private void TaskSurface_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border border) return;
        border.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        // Pointer left a paused acknowledgement — restart its fold timer (a fresh ~2s with no hover).
        if (border.DataContext is TaskRowViewModel { IsAcknowledging: true, IsRecurringCompletion: false } row)
            StartAckTimer(row);
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
            UpdateListObscured();
            var shown = panel.Visibility == Visibility.Visible;
            // A freshly opened panel should land the recurrence timeline on its live head pip; the strip's
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

    private void ListContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        var compact = _listCompact
            ? width < ListCompactWidth + LayoutHysteresis
            : width < ListCompactWidth;
        SetListCompact(compact);
    }

    private void SetListCompact(bool compact)
    {
        if (compact == _listCompact)
            return;
        _listCompact = compact;
        ViewModel.SetRowsCompact(compact);
    }

    /// <summary>
    /// Picks the master/detail arrangement for the current content width: list and panel side by side
    /// when there's room, or the panel as a full-width overlay over the list when narrow. Runs on resize.
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
    /// Moves the detail panel between its side-by-side column and a full-width overlay over the list. The
    /// panel's own opaque background covers the list in overlay mode, so closing it simply reveals the list
    /// again — the window is never resized on the user's behalf.
    /// </summary>
    private void ApplyDetailLayoutMode(bool overlay)
    {
        _detailOverlay = overlay;
        if (overlay)
        {
            Grid.SetColumn(DetailPanel, 0);
            Grid.SetColumnSpan(DetailPanel, 2);   // stretch across the list + panel columns
            DetailPanel.Width = double.NaN;
            SetDetailResizeGripVisible(false);
        }
        else
        {
            Grid.SetColumn(DetailPanel, 1);
            Grid.SetColumnSpan(DetailPanel, 1);
        }
        UpdateListObscured();
        ApplyDetailPanelWidth();   // owns the resize-handle visibility (hidden when not resizable)
    }

    /// <summary>
    /// In overlay mode the open panel sits over the list. Rather than tint or back the translucent panel —
    /// which can't reproduce the Mica-tinted surface it normally floats on — we hide the list while it's
    /// covered: the panel then composites over the bare page exactly as it does side by side, so its color
    /// is identical and nothing shows through. Opacity (not Collapsed) preserves the list's scroll state.
    /// </summary>
    private void UpdateListObscured()
    {
        var obscured = _detailOverlay && DetailPanel.Visibility == Visibility.Visible;
        ListContainer.Opacity = obscured ? 0 : 1;
        ListContainer.IsHitTestVisible = !obscured;
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
        // Persist the clamped, applied width (not the raw drag value) so the resize sticks across lists and
        // launches. Overlay mode stretches the panel to fill, so there's no user-chosen width to save there.
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
        // In overlay mode the panel stretches to fill both columns (Width = NaN); leave it be and only
        // refresh the panel's internal one-/two-column responsive state. No resizing here.
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
        if (Math.Abs(DetailPanel.Width - width) > 0.5)
            DetailPanel.Width = width;

        // The panel is resizable only when the window leaves real drag room; otherwise it's pinned at its
        // limit. The (transparent) hit area is kept permanently present — toggling its Visibility dropped
        // its hit-testing after a layout change, which is why the handle went dead after a resize — so the
        // drag start and the hover grip are gated on this flag instead, and the grip stays hidden when not
        // resizable so no dead handle ever shows.
        _detailResizable = max - min > 4;
        if (!_detailResizable)
            SetDetailResizeGripVisible(false);

        UpdateDetailResponsiveLayout();
    }

    // Whether the panel can be dragged right now, computed live from the current layout rather than read
    // from the cached _detailResizable flag. The cache is only refreshed inside ApplyDetailPanelWidth, which
    // does not run when the panel merely opens — so gating the drag start / hover grip on the cache left the
    // handle dead until the window was resized. Recomputing here keeps the handle correct the moment it's
    // touched. Returns false in overlay mode (panel fills its space, nothing to drag) or before first layout.
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

    private void UpdateDetailResponsiveLayout()
    {
        var width = DetailPanel.ActualWidth > 0 ? DetailPanel.ActualWidth : DetailPanel.Width;
        var compact = width < DetailCompactBreakpoint;
        SetResponsivePair(DetailMetaGrid, DetailGroupField, compact, new GridLength(1, GridUnitType.Star));
        SetResponsivePair(WhenDateTimeGrid, WhenTimePanel, compact, GridLength.Auto);

        // In one-column (compact) mode the time dropdowns own the full card width, so let their columns
        // go star and drop the fixed width; in two-column mode they hug the date at a fixed 72px.
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

    /// <summary>
    /// Slides the detail panel in from the right while fading up, using Files' signature pane curve
    /// (CubicBezier 0.1,0.9 0.2,1.0 over 350ms). Translation runs on the compositor thread.
    /// </summary>
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

    /// <summary>Slides the detail panel out with the matching reverse of the entry motion.</summary>
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

    /// <summary>
    /// Attaches the drag-to-reorder surface to a task <see cref="ItemsRepeater"/> the first time it
    /// loads. The surface is layout-agnostic, so the same wiring serves the standard list and each
    /// group section's repeater.
    /// </summary>
    private void ReorderRepeater_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ItemsRepeater repeater || _reorderSurfaces.TryGetValue(repeater, out _)) return;
        if (!_taskRepeaters.Contains(repeater)) _taskRepeaters.Add(repeater);
        var surface = ReorderSurface.Attach(
            repeater,
            (items, movedId) => ViewModel.PersistReorderAsync(items, movedId),
            _animationsEnabled,
            suppress => _suppressItemEntrance = suppress);
        _reorderSurfaces.Add(repeater, surface);
    }

    private void TaskRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (_suppressItemEntrance || !_animationsEnabled || args.Element is not UIElement element) return;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Scale");
        visual.Opacity = 1f;
        visual.Scale = Vector3.One;

        var delay = TimeSpan.FromMilliseconds(Math.Min(args.Index, 7) * 26);
        var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0f, 0f);
        opacity.InsertKeyFrame(1f, 1f);
        opacity.Duration = TimeSpan.FromMilliseconds(180);
        opacity.DelayTime = delay;

        var scale = visual.Compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0f, new Vector3(0.992f, 0.978f, 1f));
        scale.InsertKeyFrame(1f, Vector3.One);
        scale.Duration = TimeSpan.FromMilliseconds(220);
        scale.DelayTime = delay;

        visual.StartAnimation("Opacity", opacity);
        visual.StartAnimation("Scale", scale);
    }

    private void TaskRepeater_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        var visual = ElementCompositionPreview.GetElementVisual(args.Element);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Scale");
        if (!_animationsEnabled)
        {
            visual.Opacity = 1f;
            visual.Scale = Vector3.One;
            return;
        }

        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, visual.Opacity);
        fade.InsertKeyFrame(1f, 0f);
        fade.Duration = TimeSpan.FromMilliseconds(90);
        visual.StartAnimation("Opacity", fade);

        var settle = visual.Compositor.CreateVector3KeyFrameAnimation();
        settle.InsertKeyFrame(0f, visual.Scale);
        settle.InsertKeyFrame(1f, new Vector3(0.995f, 0.995f, 1f));
        settle.Duration = TimeSpan.FromMilliseconds(90);
        visual.StartAnimation("Scale", settle);
    }

    private void ConfigureImplicitAnimations(FrameworkElement element)
    {
        if (!_animationsEnabled) return;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (visual.ImplicitAnimations is not null) return;
        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.2f, 1f));
        var animations = compositor.CreateImplicitAnimationCollection();

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.Target = "Opacity";
        opacity.InsertExpressionKeyFrame(1f, "this.FinalValue", easing);
        opacity.Duration = TimeSpan.FromMilliseconds(150);
        animations["Opacity"] = opacity;

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.Target = "Scale";
        scale.InsertExpressionKeyFrame(1f, "this.FinalValue", easing);
        scale.Duration = TimeSpan.FromMilliseconds(150);
        animations["Scale"] = scale;

        visual.ImplicitAnimations = animations;
    }

    private static void UpdateCenterPoint(FrameworkElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.CenterPoint = new Vector3((float)element.ActualWidth / 2f, (float)element.ActualHeight / 2f, 0f);
    }

    private void EnableWhen_Click(object sender, RoutedEventArgs e) => ViewModel.Detail.EnableWhenEditor();
    private void ClearWhen_Click(object sender, RoutedEventArgs e) => ViewModel.Detail.ClearWhen();

    private static bool IsInteractiveElement(DependencyObject? element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Button or CheckBox)
                return true;
            if (current is ListViewItem)
                break;
        }
        return false;
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
        var fade = FindDescendant<Microsoft.UI.Xaml.Shapes.Rectangle>(parent, "TagNameFade");
        if (fade is null)
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

    private async void CloseDetail_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
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
    private async void DetailText_LostFocus(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => await ViewModel.Detail.FlushAsync();

    // The title commits on Enter too (it binds live, so this just flushes the save now). AcceptsReturn is
    // off, so Enter never inserts a newline.
    private async void DetailTitle_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await ViewModel.Detail.FlushAsync();
    }

    // A checklist item commits on Enter by pushing the box's current text to its row view model (the Text
    // binding otherwise updates only on focus-out), which fires the checklist save right away.
    private void ChecklistItemTitle_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || sender is not TextBox { DataContext: ChecklistItemViewModel item } box) return;
        e.Handled = true;
        item.Title = box.Text;
    }

    private async void AddChecklistItem_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => await RunSafelyAsync(() => ViewModel.Detail.AddChecklistItemCommand.ExecuteAsync(null));

    private void TagRow_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TagEditorOption option })
            ViewModel.Detail.ToggleTag(option.Id);
    }

    // The + 새 태그 affordance opens an inline field in the tag card rather than a modal: type a name
    // and press Enter (or 추가) to create + select it; Escape or blurring an empty field dismisses it.
    private void BeginAddTag_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
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

    private async void ConfirmAddTag_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => await CommitNewTagAsync();

    private async void NewTagBox_LostFocus(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
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

    private async void DeleteChecklistItem_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
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

    // --- Completion acknowledgement: the brief in-row moment after a task is ticked ---

    /// <summary>
    /// Runs the in-row acknowledgement after a completion. A terminal completion (<paramref name="nextOccurrence"/>
    /// null) shows an undo bar then folds the row away. A repeating completion (next occurrence non-null)
    /// spins its refresh glyph one turn, shows the next date, then refreshes the row in place to its next
    /// cycle — it is <i>not</i> folded, because the same-id task lives on. With animations off the row is
    /// finalized immediately (reload either drops it or updates it in place).
    /// </summary>
    private void OnCompletionAcknowledged(TaskRowViewModel row, DateOnly? nextOccurrence)
    {
        if (!_animationsEnabled)
        {
            _ = RunSafelyAsync(() => ViewModel.FinalizeCompletionAsync(row));
            return;
        }
        _ = RunAcknowledgementAsync(row, nextOccurrence);
    }

    private async Task RunAcknowledgementAsync(TaskRowViewModel row, DateOnly? nextOccurrence)
    {
        // Let the circular check pop play and the row settle into its dimmed completed state first…
        await Task.Delay(300);
        // …then swap the row body for the acknowledgement bar (an undo note for a terminal completion, or
        // a refresh spin + "다음: …" for a repeating one) and start the hold timer.
        row.BeginCompletionAcknowledgement(nextOccurrence);
        if (row.IsRecurringCompletion)
            DispatcherQueue.TryEnqueue(() => StartRefreshSpin(row));   // after the bar lays out
        StartAckTimer(row);
    }

    private void StartAckTimer(TaskRowViewModel row)
    {
        StopAckTimer(row);
        var timer = DispatcherQueue.CreateTimer();
        // A repeating completion holds briefly then rolls on; a one-off lingers ~2s so the undo is reachable.
        timer.Interval = TimeSpan.FromMilliseconds(row.IsRecurringCompletion ? 1300 : 2000);
        timer.IsRepeating = false;
        timer.Tick += async (_, _) =>
        {
            StopAckTimer(row);
            // A repeating task turns over to its next cycle in place; a terminal completion folds away.
            await RunSafelyAsync(() => row.IsRecurringCompletion
                ? RefreshRecurringRowAsync(row)
                : FoldAndFinalizeAsync(row));
        };
        _ackTimers[row] = timer;
        timer.Start();
    }

    private void StopAckTimer(TaskRowViewModel row)
    {
        if (_ackTimers.Remove(row, out var timer))
            timer.Stop();
    }

    private async Task FoldAndFinalizeAsync(TaskRowViewModel row)
    {
        if (FindRowContainer(row) is { } element)
            await AnimateRowFoldAsync(element);
        await ViewModel.FinalizeCompletionAsync(row);
    }

    /// <summary>Folds the row away — a quick scale-Y collapse to nothing with a fade, anchored at the top
    /// so it reads as the row closing up before it leaves the list.</summary>
    private static async Task AnimateRowFoldAsync(FrameworkElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.CenterPoint = new Vector3(0f, 0f, 0f);
        var compositor = visual.Compositor;
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f));

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0f, visual.Scale);
        scale.InsertKeyFrame(1f, new Vector3(1f, 0f, 1f), ease);
        scale.Duration = TimeSpan.FromMilliseconds(170);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, visual.Opacity);
        fade.InsertKeyFrame(1f, 0f, ease);
        fade.Duration = TimeSpan.FromMilliseconds(150);

        visual.StartAnimation("Scale", scale);
        visual.StartAnimation("Opacity", fade);
        await Task.Delay(180);
    }

    /// <summary>
    /// Turns a repeating task's row over to its next cycle in place instead of folding it away: the
    /// acknowledgement bar (refresh spin + "다음: …") fades out, the list reloads the same-id row with its
    /// next date and an unchecked box, and the refreshed row fades back in. On a Today list a next
    /// occurrence that has rolled out of range is dropped by the reload — there the fade-out doubles as the
    /// row's natural exit, so there is nothing to fade back in.
    /// </summary>
    private async Task RefreshRecurringRowAsync(TaskRowViewModel row)
    {
        if (FindRowContainer(row) is { } leaving)
            await AnimateRowFadeOutAsync(leaving);

        // Reconciles this same-id row to its next cycle in place (or drops it if out of range) and clears
        // the acknowledgement so the normal body — now next date, unchecked — returns.
        await ViewModel.FinalizeCompletionAsync(row);

        if (FindRowContainer(row) is { } refreshed)
            AnimateRowFadeIn(refreshed);
    }

    /// <summary>Fades a row out (a quick opacity drop) ahead of refreshing or dropping it.</summary>
    private static async Task AnimateRowFadeOutAsync(FrameworkElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        var compositor = visual.Compositor;
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f));
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, visual.Opacity);
        fade.InsertKeyFrame(1f, 0f, ease);
        fade.Duration = TimeSpan.FromMilliseconds(150);
        visual.StartAnimation("Opacity", fade);
        await Task.Delay(150);
    }

    /// <summary>Fades the refreshed row back in with a small scale settle — reusing the list's entrance
    /// feel so the next cycle reads as a fresh row arriving rather than a hard cut.</summary>
    private static void AnimateRowFadeIn(FrameworkElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Scale");
        var compositor = visual.Compositor;
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, ease);
        fade.Duration = TimeSpan.FromMilliseconds(240);

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0f, new Vector3(0.994f, 0.985f, 1f));
        scale.InsertKeyFrame(1f, Vector3.One, ease);
        scale.Duration = TimeSpan.FromMilliseconds(280);

        visual.StartAnimation("Opacity", fade);
        visual.StartAnimation("Scale", scale);
    }

    /// <summary>Spins the acknowledgement bar's refresh glyph a single full turn (repeating-task moment).</summary>
    private void StartRefreshSpin(TaskRowViewModel row)
    {
        if (FindRowContainer(row) is not { } container) return;
        if (FindDescendant<FontIcon>(container, "AckSpinIcon") is not { } icon) return;

        var visual = ElementCompositionPreview.GetElementVisual(icon);
        visual.CenterPoint = new Vector3((float)icon.ActualWidth / 2f, (float)icon.ActualHeight / 2f, 0f);
        visual.RotationAxis = new Vector3(0f, 0f, 1f);
        var spin = visual.Compositor.CreateScalarKeyFrameAnimation();
        spin.InsertKeyFrame(0f, 0f);
        spin.InsertKeyFrame(1f, 360f, visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.3f, 0f), new Vector2(0.2f, 1f)));
        spin.Duration = TimeSpan.FromMilliseconds(1000);
        visual.StartAnimation("RotationAngleInDegrees", spin);
    }

    /// <summary>Finds a task row's realized container across the open list and priority sections, or null
    /// when it is virtualized off-screen.</summary>
    private FrameworkElement? FindRowContainer(TaskRowViewModel row)
    {
        foreach (var repeater in _taskRepeaters)
        {
            if (repeater.ItemsSource is not System.Collections.IList list) continue;
            var index = list.IndexOf(row);
            if (index >= 0 && repeater.TryGetElement(index) is FrameworkElement element)
                return element;
        }
        return null;
    }

    private async void UndoCompletion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TaskRowViewModel row }) return;
        StopAckTimer(row);
        await RunSafelyAsync(() => ViewModel.UndoCompletionAsync(row));
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
            await _dialogs.TryShowAsync(new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "작업을 완료하지 못했습니다",
                Content = exception.Message,
                CloseButtonText = "확인",
            });
        }
    }
}
