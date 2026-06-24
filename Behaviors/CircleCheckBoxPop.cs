using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace Cue.Behaviors;

/// <summary>
/// Plays the circular completion pop only when a checkbox actually changes from unchecked to checked.
/// Visual states still own the checked/unchecked look; this behavior owns the celebration motion so
/// hover and pointer-state transitions cannot replay it.
/// </summary>
public static class CircleCheckBoxPop
{
    private static readonly UISettings UiSettings = new();

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(CircleCheckBoxPop), new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not CheckBox checkBox)
            return;

        if ((bool)e.NewValue)
        {
            checkBox.RenderTransformOrigin = new Point(0.5, 0.5);
            checkBox.RenderTransform = new ScaleTransform();
            checkBox.Checked += OnChecked;
        }
        else
        {
            checkBox.Checked -= OnChecked;
        }
    }

    private static void OnChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || !checkBox.IsLoaded || !UiSettings.AnimationsEnabled)
            return;

        if (checkBox.RenderTransform is not ScaleTransform scale)
        {
            scale = new ScaleTransform();
            checkBox.RenderTransformOrigin = new Point(0.5, 0.5);
            checkBox.RenderTransform = scale;
        }

        scale.ScaleX = 0.6;
        scale.ScaleY = 0.6;

        var storyboard = new Storyboard();
        AddScaleAnimation(storyboard, scale, nameof(ScaleTransform.ScaleX));
        AddScaleAnimation(storyboard, scale, nameof(ScaleTransform.ScaleY));
        storyboard.Begin();
    }

    private static void AddScaleAnimation(Storyboard storyboard, ScaleTransform target, string property)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = TimeSpan.FromMilliseconds(180),
            KeySpline = new KeySpline { ControlPoint1 = new Point(0.1, 0.9), ControlPoint2 = new Point(0.2, 1.0) },
            Value = 1.15,
        });
        animation.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = TimeSpan.FromMilliseconds(280),
            KeySpline = new KeySpline { ControlPoint1 = new Point(0.2, 0), ControlPoint2 = new Point(0, 1) },
            Value = 1.0,
        });

        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }
}
