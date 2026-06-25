using System.Collections.Generic;
using System.Linq;
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

/// <summary>One affirmative action in a <see cref="ConfirmPopover.ShowChoiceAsync"/> popover.</summary>
/// <param name="Text">Button label.</param>
/// <param name="Destructive">When true the button is tinted with the critical (red) tone; otherwise
/// it keeps the stock accent.</param>
public sealed record ChoicePopoverAction(string Text, bool Destructive = false);

/// <summary>Message + ordered affirmative actions for a multi-choice confirm popover (e.g. delete a
/// group: 그룹만 제거 / 할 일까지 삭제). A cancel is always added; Enter triggers
/// <see cref="DefaultActionIndex"/>.</summary>
public sealed class ChoicePopoverOptions
{
    public string Message { get; init; } = "";
    public string CancelText { get; init; } = "취소";
    public IList<ChoicePopoverAction> Actions { get; } = new List<ChoicePopoverAction>();

    /// <summary>Which action is focused and fired by Enter — the least-destructive default.</summary>
    public int DefaultActionIndex { get; init; }

    public FlyoutPlacementMode Placement { get; init; } = FlyoutPlacementMode.Bottom;
}

/// <summary>
/// A lightweight, anchored confirmation popover — a contextual alternative to a centered
/// <see cref="ContentDialog"/> for one-line confirmations. An affirmative button or Enter confirms;
/// the cancel button, Esc, or a click outside cancels. Styling is token-driven: see DesignTokens.xaml
/// (CueConfirmPopoverPresenterStyle, the CuePopover*/CuePadPopover sizing, and CueDangerFillBrush).
/// </summary>
public static class ConfirmPopover
{
    /// <summary>Shows a single-confirm popover anchored to <paramref name="target"/>; resolves true on
    /// confirm, false on any form of cancel (button, Esc, or light dismiss).</summary>
    public static async Task<bool> ShowAsync(FrameworkElement target, ConfirmPopoverOptions? options = null)
    {
        options ??= new ConfirmPopoverOptions();
        var chosen = await ShowCoreAsync(
            target,
            options.Message,
            options.CancelText,
            new[] { new ChoicePopoverAction(options.ConfirmText, options.Destructive) },
            defaultIndex: 0,
            options.Placement);
        return chosen == 0;
    }

    /// <summary>Shows a popover with several affirmative actions anchored to <paramref name="target"/>;
    /// resolves the chosen action's index, or null on any form of cancel.</summary>
    public static Task<int?> ShowChoiceAsync(FrameworkElement target, ChoicePopoverOptions options)
        => ShowCoreAsync(target, options.Message, options.CancelText, options.Actions.ToList(),
            options.DefaultActionIndex, options.Placement);

    private static Task<int?> ShowCoreAsync(
        FrameworkElement target, string message, string cancelText,
        IReadOnlyList<ChoicePopoverAction> actions, int defaultIndex, FlyoutPlacementMode placement)
    {
        var resources = Application.Current.Resources;
        var tcs = new TaskCompletionSource<int?>();

        var messageBlock = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = (double)resources["CueFontRow"],
            Foreground = (Brush)resources["TextFillColorPrimaryBrush"],
            // Popup content lives in the popup root, outside the page's FontFamily inheritance — pin
            // Pretendard so this message never falls back to the system font.
            FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)resources["CueFontFamily"],
        };

        var cancel = new Button
        {
            Content = cancelText,
            Style = (Style)resources["CueSubtleTextButtonStyle"],
            MinWidth = 60,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = (double)resources["CueGap8"],
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(cancel);

        var actionButtons = new List<Button>();
        foreach (var action in actions)
        {
            var button = new Button
            {
                Content = action.Text,
                Style = (Style)resources["AccentButtonStyle"],
                MinWidth = 60,
            };
            // A destructive action recolors only its own button to the critical tone. The AccentButton
            // template binds its background via {ThemeResource AccentButtonBackground}, which the stock
            // theme defines as a StaticResource alias of AccentFillColorDefaultBrush — overriding the
            // alias wouldn't reach it, so override the AccentButtonBackground keys directly, in this
            // button's own scope, keeping the accent template's white text and hover/press deepening.
            if (action.Destructive)
            {
                var danger = ((SolidColorBrush)resources["CueDangerFillBrush"]).Color;
                button.Resources["AccentButtonBackground"] = new SolidColorBrush(danger);
                button.Resources["AccentButtonBackgroundPointerOver"] = new SolidColorBrush(danger) { Opacity = 0.9 };
                button.Resources["AccentButtonBackgroundPressed"] = new SolidColorBrush(danger) { Opacity = 0.8 };
                // Force white label across all states: the stock accent foreground (TextOnAccentFillColor)
                // resolves to near-black in dark theme, which is unreadable on the red fill. White reads
                // on both the light (#C42B1C) and dark (#D13438) danger reds.
                var onDanger = new SolidColorBrush(Microsoft.UI.Colors.White);
                button.Foreground = onDanger;
                button.Resources["AccentButtonForeground"] = onDanger;
                button.Resources["AccentButtonForegroundPointerOver"] = onDanger;
                button.Resources["AccentButtonForegroundPressed"] = onDanger;
            }
            actionButtons.Add(button);
            buttons.Children.Add(button);
        }

        var root = new StackPanel { Spacing = (double)resources["CueGap16"] };
        root.Children.Add(messageBlock);
        root.Children.Add(buttons);

        var flyout = new Flyout
        {
            Content = root,
            Placement = placement,
            FlyoutPresenterStyle = (Style)resources["CueConfirmPopoverPresenterStyle"],
        };

        // Resolve exactly once: button click, Enter/Esc, or light dismiss all funnel here so the
        // Closed handler can't overwrite an already-made decision.
        var settled = false;
        void Settle(int? result)
        {
            if (settled) return;
            settled = true;
            tcs.TrySetResult(result);
            flyout.Hide();
        }

        cancel.Click += (_, _) => Settle(null);
        for (var i = 0; i < actionButtons.Count; i++)
        {
            var index = i;
            actionButtons[i].Click += (_, _) => Settle(index);
        }
        root.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter) { e.Handled = true; Settle(defaultIndex); }
            else if (e.Key == VirtualKey.Escape) { e.Handled = true; Settle(null); }
        };
        // Focus the default (least-destructive) action so Enter confirms and the popover is keyboard-
        // reachable at once.
        flyout.Opened += (_, _) =>
        {
            if (defaultIndex >= 0 && defaultIndex < actionButtons.Count)
                actionButtons[defaultIndex].Focus(FocusState.Programmatic);
        };
        flyout.Closed += (_, _) =>
        {
            if (settled) return;
            settled = true;
            tcs.TrySetResult(null);
        };

        flyout.ShowAt(target);
        return tcs.Task;
    }
}
