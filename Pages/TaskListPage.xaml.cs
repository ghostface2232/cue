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
        ConfigureImplicitAnimations(panel, animateOffset: true);
        var visual = ElementCompositionPreview.GetElementVisual(panel);
        panel.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            visual.Opacity = panel.Visibility == Visibility.Visible ? 1f : 0f;
            visual.Offset = panel.Visibility == Visibility.Visible ? Vector3.Zero : new Vector3(18f, 0f, 0f);
        });
        visual.Opacity = 1f;
        visual.Offset = Vector3.Zero;
    }

    private void TaskRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (!_animationsEnabled || args.Element is not UIElement element) return;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Offset");
        visual.Opacity = 1f;
        visual.Offset = Vector3.Zero;

        var delay = TimeSpan.FromMilliseconds(Math.Min(args.Index, 7) * 26);
        var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0f, 0f);
        opacity.InsertKeyFrame(1f, 1f);
        opacity.Duration = TimeSpan.FromMilliseconds(180);
        opacity.DelayTime = delay;

        var offset = visual.Compositor.CreateVector3KeyFrameAnimation();
        offset.InsertKeyFrame(0f, new Vector3(0f, 8f, 0f));
        offset.InsertKeyFrame(1f, Vector3.Zero);
        offset.Duration = TimeSpan.FromMilliseconds(220);
        offset.DelayTime = delay;

        visual.StartAnimation("Opacity", opacity);
        visual.StartAnimation("Offset", offset);
    }

    private void TaskRepeater_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        var visual = ElementCompositionPreview.GetElementVisual(args.Element);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Offset");
        if (!_animationsEnabled)
        {
            visual.Opacity = 1f;
            visual.Offset = Vector3.Zero;
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

    private void ConfigureImplicitAnimations(FrameworkElement element, bool animateOffset = false)
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

        if (animateOffset)
        {
            var offset = compositor.CreateVector3KeyFrameAnimation();
            offset.Target = "Offset";
            offset.InsertExpressionKeyFrame(1f, "this.FinalValue", easing);
            offset.Duration = TimeSpan.FromMilliseconds(180);
            animations["Offset"] = offset;
        }

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

    private void CloseDetail_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => ViewModel.Detail.Close();

    private async void SaveDetail_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => await RunSafelyAsync(() => ViewModel.Detail.SaveCommand.ExecuteAsync(null));

    private async void AddSubtask_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => await RunSafelyAsync(() => ViewModel.Detail.AddSubtaskCommand.ExecuteAsync(null));

    private async void AddLabel_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        await RunSafelyAsync(async () =>
        {
            var name = await PromptNameAsync("새 라벨", "라벨 이름");
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
            Title = "서브태스크를 삭제할까요?",
            Content = "파일은 지우지 않고 삭제 시각이 기록됩니다.",
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
            Content = "섹션의 작업은 삭제하지 않고 Cue Inbox로 이동합니다.",
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
