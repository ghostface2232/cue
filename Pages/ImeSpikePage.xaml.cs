// ============================================================================================
// SPIKE — THROWAWAY CODE (Inline-highlight plan, Step 0: IME composition signal gate).
//
// Purpose: prove, by direct observation on Windows with a real Korean IME, the three load-bearing
// assumptions the rest of the plan rests on:
//   (1) Do TextCompositionStarted/Changed/Ended actually fire for the Korean IME, and can we hold a
//       clean "is composing now?" window from them?
//   (2) When we re-tint on a commit/space/idle trigger, does the caret jump, and does the native
//       Ctrl+Z (undo) stack break?
//   (3) Does character formatting leak onto a mid-composition syllable (ㄱ→가→갈)?
//
// v2 changes (after first observation run):
//   - DIFF-ONLY re-tint: never reformat the whole document. Compute the desired accent spans, diff
//     against what is already painted, and only recolor what changed. An empty diff creates NO undo
//     unit at all. This directly tests whether the Ctrl+Z freeze was caused by full-document reformat
//     churn piling up in the native undo stack.
//   - COMPOSITION-PREFIX tinting: instead of suppressing all tinting while composing, we tint the
//     already-committed prefix and never touch the active composition span. This lets earlier tokens
//     light up live (no space required) while still never formatting a half-formed syllable.
//
// Self-contained: no DI, no view model, no parser. Tinting uses a deliberately dumb throwaway regex
// just so there is *something* positional to color — it is NOT the real Step 1 token contract. All
// output is on-screen so no debugger is needed. Delete after Step 0 is decided.
// ============================================================================================
using System;
using System.Collections.Generic;
using System.Linq;
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

    // The spans currently painted accent, so we can diff and only touch what changed.
    private readonly HashSet<(int Start, int Len)> _painted = new();

    private bool _isComposing;
    private bool _tinting;          // re-entrancy guard: ignore TextChanged raised by our own ops
    private int _compStart;         // document offset where the active composition begins
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

        Log("ready (v2: diff-only re-tint, composition-prefix tinting). type Korean above.");
        UpdateState("-");
    }

    // ---- IME composition signal (assumption 1) -------------------------------------------------

    private void OnCompositionStarted(RichEditBox sender, TextCompositionStartedEventArgs args)
    {
        _isComposing = true;
        _compositionCount++;
        _compStart = Box.Document.Selection.StartPosition;
        Log($"CompositionStarted  #{_compositionCount}  at={_compStart}");
        UpdateState("composition-start");
    }

    private void OnCompositionChanged(RichEditBox sender, TextCompositionChangedEventArgs args)
    {
        // args carries the in-progress span; remember its start so we never paint over the active tail.
        try { _compStart = args.StartIndex; } catch { /* keep last */ }
        Log($"CompositionChanged  start={Safe(() => args.StartIndex.ToString())} len={Safe(() => args.Length.ToString())}");
        UpdateState("composition-change");

        if (GateOnNonComposing.IsChecked == true)
            Tint("during-composition (committed prefix only)"); // paints [0, _compStart) — never the tail
        else
            Tint("DURING-composition (gate off — leak probe)"); // paints everything, including the tail
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

        // Space is a hard delimiter (and not a composing key): tint immediately.
        if (text.EndsWith(" ", StringComparison.Ordinal))
        {
            _idle.Stop();
            Tint("space");
            return;
        }

        // Otherwise (re)arm the idle timer; it fires the tint after a typing pause.
        _idle.Stop();
        _idle.Start();
    }

    // The document offset up to which we are allowed to paint: everything when not composing, or just
    // the committed prefix (excluding the active composition tail) while composing under the gate.
    private int PaintLimit(string text)
    {
        if (_isComposing && GateOnNonComposing.IsChecked == true)
            return Math.Clamp(_compStart, 0, text.Length);
        return text.Length;
    }

    // ---- Re-tint: DIFF ONLY (assumptions 2 & 3) ------------------------------------------------

    private void Tint(string reason)
    {
        try
        {
            var doc = Box.Document;
            var text = GetPlainText();
            int limit = PaintLimit(text);

            // Desired accent spans within the paintable range.
            var desired = new HashSet<(int Start, int Len)>();
            foreach (Match m in DateIsh.Matches(text))
                if (m.Index + m.Length <= limit)
                    desired.Add((m.Index, m.Length));

            var toAdd = desired.Where(s => !_painted.Contains(s)).ToList();
            var toClear = _painted.Where(s => !desired.Contains(s)).ToList();

            if (toAdd.Count == 0 && toClear.Count == 0)
            {
                // Nothing changed — do NOT create an (empty) undo unit. This is the key anti-churn move.
                return;
            }

            int caretStart = doc.Selection.StartPosition;
            int caretEnd = doc.Selection.EndPosition;
            bool group = IsolateUndoGroup.IsChecked == true;

            _tinting = true;
            doc.BatchDisplayUpdates();
            try
            {
                if (group) doc.BeginUndoGroup();
                foreach (var s in toClear)
                    doc.GetRange(s.Start, s.Start + s.Len).CharacterFormat.ForegroundColor = DefaultColor();
                foreach (var s in toAdd)
                    doc.GetRange(s.Start, s.Start + s.Len).CharacterFormat.ForegroundColor = Accent;
            }
            finally
            {
                if (group) doc.EndUndoGroup();   // always paired, even if a format op throws
                doc.Selection.SetRange(caretStart, caretEnd);
                doc.ApplyDisplayUpdates();
                _tinting = false;
            }

            _painted.Clear();
            foreach (var s in desired) _painted.Add(s);

            int afterStart = doc.Selection.StartPosition;
            int afterEnd = doc.Selection.EndPosition;
            var jumped = (afterStart != caretStart || afterEnd != caretEnd) ? "  <<< CARET MOVED" : "";
            Log($"  tint [{reason}] +{toAdd.Count}/-{toClear.Count} (painted={_painted.Count})  caret {caretStart},{caretEnd} -> {afterStart},{afterEnd}{jumped}");
            UpdateState(reason);
        }
        catch (Exception ex)
        {
            _tinting = false;
            Log($"  tint [{reason}] THREW: {ex.GetType().Name}: {ex.Message}");
        }
    }

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
                $"IsComposing={_isComposing}  compStart={_compStart}  caret=({doc.Selection.StartPosition},{doc.Selection.EndPosition})  " +
                $"len={GetPlainText().Length}  painted={_painted.Count}  compositions={_compositionCount}  lastTrigger={lastTrigger}";
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
        _painted.Clear();
    }
}
