using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI.ViewManagement;
using Cue.ViewModels;

namespace Cue.Pages;

/// <summary>A horizontally scrolling month timeline over the index-backed task date projection.</summary>
public sealed partial class TimelinePage : Page
{
    private readonly bool _animationsEnabled = new UISettings().AnimationsEnabled;

    public TimelineViewModel ViewModel { get; }

    public TimelinePage()
    {
        ViewModel = App.Services.GetRequiredService<TimelineViewModel>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await RunSafelyAsync(() => ViewModel.LoadCommand.ExecuteAsync(null));
    }

    private async void PreviousMonth_Click(object sender, RoutedEventArgs e)
        => await RunSafelyAsync(() => ViewModel.PreviousMonthCommand.ExecuteAsync(null));

    private async void Today_Click(object sender, RoutedEventArgs e)
        => await RunSafelyAsync(() => ViewModel.GoTodayCommand.ExecuteAsync(null));

    private async void NextMonth_Click(object sender, RoutedEventArgs e)
        => await RunSafelyAsync(() => ViewModel.NextMonthCommand.ExecuteAsync(null));

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
