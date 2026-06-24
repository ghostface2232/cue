using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Cue.ViewModels;
using Cue.Services;
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
    }

    private void TaskSurface_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border border) return;
        border.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
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
        if (!_detailResizable) return;   // pinned at its limit, or overlay — nothing to drag
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
        if (_detailResizable)
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
