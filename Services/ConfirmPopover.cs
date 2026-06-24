using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Cue.Services;

/// <summary>Text + placement for a <see cref="ConfirmPopover"/>. Defaults read as a delete confirm.</summary>
public sealed class ConfirmPopoverOptions
{
    public string Message { get; init; } = "삭제하시겠습니까?";
    public string ConfirmText { get; init; } = "삭제";
    public string CancelText { get; init; } = "취소";

    /// <summary>When true the primary button is tinted with the critical (red) tone.</summary>
    public bool Destructive { get; init; } = true;

    public FlyoutPlacementMode Placement { get; init; } = FlyoutPlacementMode.Bottom;
}

/// <summary>
/// A lightweight, anchored confirmation popover — a contextual alternative to a centered
/// <see cref="ContentDialog"/> for one-line confirmations. The primary button or Enter confirms;
/// the cancel button, Esc, or a click outside cancels. Styling is token-driven: see DesignTokens.xaml
/// (CueConfirmPopoverPresenterStyle, the CuePopover*/CuePadPopover sizing, and CueDangerFillBrush).
/// </summary>
public static class ConfirmPopover
{
    /// <summary>Shows the popover anchored to <paramref name="target"/>; resolves true on confirm,
    /// false on any form of cancel (button, Esc, or light dismiss).</summary>
    public static Task<bool> ShowAsync(FrameworkElement target, ConfirmPopoverOptions? options = null)
    {
        options ??= new ConfirmPopoverOptions();
        var resources = Application.Current.Resources;
        var tcs = new TaskCompletionSource<bool>();

        var message = new TextBlock
        {
            Text = options.Message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = (double)resources["CueFontRow"],
            Foreground = (Brush)resources["TextFillColorPrimaryBrush"],
        };

        var cancel = new Button
        {
            Content = options.CancelText,
            Style = (Style)resources["CueSubtleTextButtonStyle"],
            MinWidth = 60,
        };
        var confirm = new Button
        {
            Content = options.ConfirmText,
            Style = (Style)resources["AccentButtonStyle"],
            MinWidth = 60,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = (double)resources["CueGap8"],
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);

        var root = new StackPanel { Spacing = (double)resources["CueGap16"] };
        root.Children.Add(message);
        root.Children.Add(buttons);

        if (options.Destructive)
        {
            // Recolor the AccentButton template to the critical tone within the popover scope only,
            // so the primary button reads as destructive (red) while keeping the stock accent
            // template's white-on-fill text and hover/press deepening.
            var danger = ((SolidColorBrush)resources["CueDangerFillBrush"]).Color;
            root.Resources["AccentFillColorDefaultBrush"] = new SolidColorBrush(danger);
            root.Resources["AccentFillColorSecondaryBrush"] = new SolidColorBrush(danger) { Opacity = 0.9 };
            root.Resources["AccentFillColorTertiaryBrush"] = new SolidColorBrush(danger) { Opacity = 0.8 };
        }

        var flyout = new Flyout
        {
            Content = root,
            Placement = options.Placement,
            FlyoutPresenterStyle = (Style)resources["CueConfirmPopoverPresenterStyle"],
        };

        // Resolve exactly once: button click, Enter/Esc, or light dismiss all funnel here so the
        // Closed handler can't overwrite an already-made decision.
        var settled = false;
        void Settle(bool result)
        {
            if (settled) return;
            settled = true;
            tcs.TrySetResult(result);
            flyout.Hide();
        }

        confirm.Click += (_, _) => Settle(true);
        cancel.Click += (_, _) => Settle(false);
        root.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter) { e.Handled = true; Settle(true); }
            else if (e.Key == VirtualKey.Escape) { e.Handled = true; Settle(false); }
        };
        // Focus the primary button so Enter confirms and the popover is keyboard-reachable at once.
        flyout.Opened += (_, _) => confirm.Focus(FocusState.Programmatic);
        flyout.Closed += (_, _) =>
        {
            if (settled) return;
            settled = true;
            tcs.TrySetResult(false);
        };

        flyout.ShowAt(target);
        return tcs.Task;
    }
}
