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
    public TaskListViewModel ViewModel { get; }
    private readonly DialogService _dialogs;
    private readonly bool _animationsEnabled = new UISettings().AnimationsEnabled;
    private readonly ConditionalWeakTable<FrameworkElement, DropShadow> _iconGlows = new();
    private readonly ConditionalWeakTable<ItemsRepeater, ReorderSurface> _reorderSurfaces = new();
    private Visual? _detailPanelVisual;

    // Set while a drag-reorder commits, so the row that moves in the bound collection does not also
    // play the list's entrance animation on top of the drop settle.
    private bool _suppressItemEntrance;

    public TaskListPage()
    {
        ViewModel = App.Services.GetRequiredService<TaskListViewModel>();
        _dialogs = App.Services.GetRequiredService<DialogService>();
        InitializeComponent();
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
                    : TaskListMode.Inbox;
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
        if (sender is not FrameworkElement { Tag: Guid id } || IsInteractiveElement(e.OriginalSource as DependencyObject))
            return;
        e.Handled = true;
        await RunSafelyAsync(() => ViewModel.SelectTaskCommand.ExecuteAsync(id));
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
        if (_animationsEnabled)
            ElementCompositionPreview.GetElementVisual(border).Scale = new Vector3(1.0025f, 1.0025f, 1f);
    }

    private void TaskSurface_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border border) return;
        border.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ElementCompositionPreview.GetElementVisual(border).Scale = Vector3.One;
    }

    private void IconAction_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        ConfigureImplicitAnimations(element);
        UpdateCenterPoint(element);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.Scale = new Vector3(1.1f, 1.1f, 1f);
        visual.Opacity = 0.82f;
        var glow = EnsureIconGlow(element);
        glow.BlurRadius = 12f;
        glow.Opacity = 0.28f;
    }

    private void IconAction_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.Scale = Vector3.One;
        visual.Opacity = 1f;
        if (_iconGlows.TryGetValue(element, out var glow))
        {
            glow.BlurRadius = 0f;
            glow.Opacity = 0f;
        }
    }

    private void DetailPanel_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement panel) return;
        ElementCompositionPreview.SetIsTranslationEnabled(panel, true);
        var visual = ElementCompositionPreview.GetElementVisual(panel);
        _detailPanelVisual = visual;
        visual.Opacity = panel.Visibility == Visibility.Visible ? 1f : 0f;

        panel.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
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
    /// loads. The surface is layout-agnostic, so the same wiring serves the standard list, the This
    /// Evening section, and each project section's repeater.
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

    private DropShadow EnsureIconGlow(FrameworkElement element)
    {
        if (_iconGlows.TryGetValue(element, out var existing)) return existing;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var glowVisual = compositor.CreateSpriteVisual();
        glowVisual.Size = new Vector2(18f, 18f);
        glowVisual.Offset = new Vector3(
            Math.Max(0f, ((float)element.ActualWidth - 18f) / 2f),
            Math.Max(0f, ((float)element.ActualHeight - 18f) / 2f),
            0f);
        glowVisual.Brush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(1, 255, 255, 255));

        var shadow = compositor.CreateDropShadow();
        shadow.Color = element is Control { Foreground: Microsoft.UI.Xaml.Media.SolidColorBrush brush }
            ? brush.Color
            : Microsoft.UI.Colors.Gray;
        shadow.BlurRadius = 0f;
        shadow.Opacity = 0f;
        glowVisual.Shadow = shadow;

        if (_animationsEnabled)
        {
            var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.2f, 1f));
            var implicitAnimations = compositor.CreateImplicitAnimationCollection();
            var blur = compositor.CreateScalarKeyFrameAnimation();
            blur.Target = "BlurRadius";
            blur.InsertExpressionKeyFrame(1f, "this.FinalValue", easing);
            blur.Duration = TimeSpan.FromMilliseconds(160);
            implicitAnimations["BlurRadius"] = blur;
            var opacity = compositor.CreateScalarKeyFrameAnimation();
            opacity.Target = "Opacity";
            opacity.InsertExpressionKeyFrame(1f, "this.FinalValue", easing);
            opacity.Duration = TimeSpan.FromMilliseconds(140);
            implicitAnimations["Opacity"] = opacity;
            shadow.ImplicitAnimations = implicitAnimations;
        }

        ElementCompositionPreview.SetElementChildVisual(element, glowVisual);
        _iconGlows.Add(element, shadow);
        return shadow;
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

    private async void CloseDetail_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => await CloseDetailWithAnimationAsync();

    private async Task CloseDetailWithAnimationAsync()
    {
        if (!_animationsEnabled || _detailPanelVisual is null)
        {
            ViewModel.Detail.Close();
            return;
        }

        AnimateDetailPanelOut(_detailPanelVisual);
        await Task.Delay(170);
        ViewModel.Detail.Close();
    }

    private async void SaveDetail_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => await RunSafelyAsync(() => ViewModel.Detail.SaveCommand.ExecuteAsync(null));

    private async void AddSubtask_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => await RunSafelyAsync(() => ViewModel.Detail.AddSubtaskCommand.ExecuteAsync(null));

    private void LabelRow_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LabelEditorOption option })
            ViewModel.Detail.ToggleLabel(option.Id);
    }

    private async void AddLabel_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        await RunSafelyAsync(async () =>
        {
            var name = await PromptNameAsync("새 태그", "태그 이름");
            if (name is not null)
                await ViewModel.Detail.AddLabelCommand.ExecuteAsync(name);
        });
    }

    private async void OpenParent_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => await RunSafelyAsync(() => ViewModel.Detail.OpenParentCommand.ExecuteAsync(null));

    private async void OpenSubtask_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid id })
            await RunSafelyAsync(() => ViewModel.Detail.OpenSubtaskCommand.ExecuteAsync(id));
    }

    private async void DeleteSubtask_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "체크리스트 항목을 삭제할까요?",
            Content = "파일은 지우지 않고 삭제 시각만 기록됩니다.",
            PrimaryButtonText = "삭제",
            CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Close,
        };
        await RunSafelyAsync(async () =>
        {
            if (await _dialogs.ShowAsync(dialog) == ContentDialogResult.Primary)
                await ViewModel.Detail.DeleteSubtaskCommand.ExecuteAsync(id);
        });
    }

    private async void AddSection_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        await RunSafelyAsync(async () =>
        {
            var name = await PromptNameAsync("새 섹션", "섹션 이름");
            if (name is not null) await ViewModel.CreateSectionCommand.ExecuteAsync(name);
        });
    }

    private async void RenameSection_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        await RunSafelyAsync(async () =>
        {
            if (sender is not Button { Tag: Guid id }) return;
            var group = ViewModel.ProjectGroups.FirstOrDefault(item => item.Id == id);
            if (group is null) return;
            var name = await PromptNameAsync("섹션 이름 변경", "섹션 이름", group.Name);
            if (name is not null) await ViewModel.RenameSectionCommand.ExecuteAsync(new RenameRecordRequest(id, name));
        });
    }

    private async void DeleteSection_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "섹션을 삭제할까요?",
            Content = "섹션 안의 할 일은 지우지 않고 Cue로 옮깁니다.",
            PrimaryButtonText = "삭제",
            CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Close,
        };
        await RunSafelyAsync(async () =>
        {
            if (await _dialogs.ShowAsync(dialog) == ContentDialogResult.Primary)
                await ViewModel.DeleteSectionCommand.ExecuteAsync(id);
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
