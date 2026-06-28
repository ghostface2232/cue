// ============================================================================================
// SPIKE — THROWAWAY CODE (Inline-highlight plan, Step 0: IME composition signal gate).
//
// Purpose: prove, by direct observation on Windows with a real Korean IME, the three load-bearing
// assumptions the rest of the plan rests on:
//   (1) Do TextCompositionStarted/Changed/Ended actually fire for the Korean IME, and can we hold a
//       clean "is composing now?" window from them?
//   (2) When we re-tint the whole range on a commit/space/idle trigger, does the caret jump, and does
//       the native Ctrl+Z (undo) stack break?
//   (3) Does character formatting leak onto a mid-composition syllable (ㄱ→가→갈)?
//
// Everything is self-contained: no DI, no view model, no parser. Tinting uses a deliberately dumb
// throwaway regex just so there is *something* positional to color — it is NOT the real Step 1 token
// contract. All output is on-screen so no debugger is needed. Delete after Step 0 is decided.
// ============================================================================================
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace Cue.Pages;

public sealed partial class ImeSpikePage : Page
{
    // Dumb throwaway "looks date-ish" matcher. Enough to paint visible accent spans so caret/leak/undo
    // behaviour is observable. Real tokenization is Step 1 and lives in the parser, not here.
    private static readonly Regex DateIsh = new(
        @"\d+\s*(?:시|분|일|월|주|시간)|오늘|내일|모레|글피|매주|매일|매월|매년|격주|평일|마다|" +
        @"[월화수목금토일]요일|[월화수목금토일]욜|오전|오후|아침|점심|저녁|새벽|낮|밤",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Color Accent = new() { A = 255, R = 0xE0, G = 0x6C, B = 0x00 };

    private readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly List<string> _log = new();
    private bool _isComposing;
    private bool _tinting; // re-entrancy guard: ignore TextChanged raised by our own formatting ops
    private int _compositionCount;

    public ImeSpikePage()
    {
        InitializeComponent();

        Box.TextChanged += OnTextChanged;
        Box.TextCompositionStarted += OnCompositionStarted;
        Box.TextCompositionChanged += OnCompositionChanged;
        Box.TextCompositionEnded += OnCompositionEnded;

        _idle.Tick += (_, _) =>
        {
            _idle.Stop();
            Tint("idle-500ms");
        };

        Log("ready. type Korean into the box above.");
        UpdateState("-");
    }

    // ---- IME composition signal (assumption 1) -------------------------------------------------

    private void OnCompositionStarted(RichEditBox sender, TextCompositionStartedEventArgs args)
    {
        _isComposing = true;
        _compositionCount++;
        Log($"CompositionStarted  #{_compositionCount}");
        UpdateState("composition-start");
    }

    private void OnCompositionChanged(RichEditBox sender, TextCompositionChangedEventArgs args)
    {
        // args carries the in-progress span; log it so we can see whether the signal is usable.
        var span = Safe(() => $"start={args.StartIndex} len={args.Length}");
        Log($"CompositionChanged  {span}");
        UpdateState("composition-change");

        // Leak probe: when the gate is OFF we deliberately tint mid-composition so the observer can
        // see whether the accent bleeds onto the half-formed syllable.
        if (GateOnNonComposing.IsChecked != true)
            Tint("DURING-composition (gate off)");
    }

    private void OnCompositionEnded(RichEditBox sender, TextCompositionEndedEventArgs args)
    {
        _isComposing = false;
        Log("CompositionEnded -> commit");
        UpdateState("composition-end");
        Tint("commit");
    }

    // ---- Trigger plumbing ----------------------------------------------------------------------

    private void OnTextChanged(object sender, RoutedEventArgs e)
    {
        if (_tinting)
            return; // our own re-tint; ignore (re-entrancy guard)

        var text = GetPlainText();
        UpdateState("text-changed");

        // Space-triggered tint, gated on non-composing.
        if (text.EndsWith(" ", StringComparison.Ordinal) && CanTintNow())
        {
            _idle.Stop();
            Tint("space");
            return;
        }

        // Otherwise (re)arm the idle timer; it fires the tint after a typing pause.
        _idle.Stop();
        _idle.Start();
    }

    private bool CanTintNow() => GateOnNonComposing.IsChecked != true || !_isComposing;

    // ---- Re-tint: full reset + re-color matched spans (assumptions 2 & 3) ----------------------

    private void Tint(string reason)
    {
        if (!CanTintNow())
        {
            Log($"  (skip tint [{reason}] — composing)");
            return;
        }

        try
        {
            var doc = Box.Document;
            var text = GetPlainText();

            // Save caret BEFORE so we can observe whether re-tint moved it.
            int caretStart = doc.Selection.StartPosition;
            int caretEnd = doc.Selection.EndPosition;

            _tinting = true;
            doc.BatchDisplayUpdates();
            if (IsolateUndoGroup.IsChecked == true)
                doc.BeginUndoGroup();

            // 1) reset whole range to the theme default colour
            var all = doc.GetRange(0, TextRangeEnd);
            all.CharacterFormat.ForegroundColor = DefaultColor();

            // 2) re-color every date-ish match (BMP Korean => plain offset == document offset)
            int painted = 0;
            foreach (Match m in DateIsh.Matches(text))
            {
                var r = doc.GetRange(m.Index, m.Index + m.Length);
                r.CharacterFormat.ForegroundColor = Accent;
                painted++;
            }

            if (IsolateUndoGroup.IsChecked == true)
                doc.EndUndoGroup();

            // restore caret AFTER
            doc.Selection.SetRange(caretStart, caretEnd);
            doc.ApplyDisplayUpdates();
            _tinting = false;

            int afterStart = doc.Selection.StartPosition;
            int afterEnd = doc.Selection.EndPosition;
            var jumped = (afterStart != caretStart || afterEnd != caretEnd) ? "  <<< CARET MOVED" : "";
            Log($"  tint [{reason}] painted={painted}  caret {caretStart},{caretEnd} -> {afterStart},{afterEnd}{jumped}");
            UpdateState(reason);
        }
        catch (Exception ex)
        {
            _tinting = false;
            Log($"  tint [{reason}] THREW: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // A large sentinel end position; ITextRange clamps to the document end.
    private const int TextRangeEnd = int.MaxValue;

    private Color DefaultColor()
        => ActualTheme == ElementTheme.Dark
            ? new Color { A = 255, R = 0xF2, G = 0xF2, B = 0xF2 }
            : new Color { A = 255, R = 0x1A, G = 0x1A, B = 0x1A };

    // ---- helpers -------------------------------------------------------------------------------

    private string GetPlainText()
    {
        try
        {
            Box.Document.GetText(TextGetOptions.NoHidden, out var s);
            // RichEditBox appends a trailing \r; drop a single one so offsets line up with what's typed.
            return s.TrimEnd('\r');
        }
        catch
        {
            return string.Empty;
        }
    }

    private void UpdateState(string lastTrigger)
    {
        try
        {
            var doc = Box.Document;
            StateLine.Text =
                $"IsComposing={_isComposing}  caret=({doc.Selection.StartPosition},{doc.Selection.EndPosition})  " +
                $"len={GetPlainText().Length}  compositions={_compositionCount}  lastTrigger={lastTrigger}";
        }
        catch { /* never let telemetry throw */ }
    }

    private void Log(string line)
    {
        _log.Add(line);
        if (_log.Count > 250)
            _log.RemoveRange(0, _log.Count - 250);
        LogText.Text = string.Join("\n", _log);
        LogScroll.UpdateLayout();
        LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, disableAnimation: true);
    }

    private static string Safe(Func<string> f)
    {
        try { return f(); } catch { return "(n/a)"; }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _log.Clear();
        LogText.Text = string.Empty;
    }
}
