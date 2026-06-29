// ============================================================================================
// SPIKE — THROWAWAY CODE (Inline-highlight plan, Step 0: IME composition signal gate).
//
// Purpose: prove, by direct observation on Windows with a real Korean IME, the three load-bearing
// assumptions the rest of the plan rests on:
//   (1) Do TextCompositionStarted/Changed/Ended actually fire for the Korean IME, and can we hold a
//       clean "is composing now?" window from them?
//   (2) When we re-tint on a commit/space/idle trigger, does the caret jump, and does the native
//       Ctrl+Z (undo) stack break? (Surfaced as cumulative "undo units" + "caret jumps" counters.)
//   (3) Does character formatting leak onto a mid-composition syllable (ㄱ→가→갈)?
//
// Telemetry is a GLANCEABLE DASHBOARD, not a scrolling firehose: a colour-coded verdict row, a live
// state line, cumulative counters, and a short newest-first feed of NOTABLE events only (the noisy
// per-keystroke CompositionChanged is aggregated into a counter, never printed line-by-line). This
// keeps the UI cheap (no giant string rebuilds, no layout thrash) and easy to read.
//
// Re-tint is DIFF-ONLY: compute the desired accent spans, diff against what is already painted, and
// only recolor the delta — an empty diff creates NO undo unit. Tinting also runs during composition
// over the committed prefix only, never the active tail. Tinting uses a deliberately dumb throwaway
// regex (NOT the real Step 1 token contract). Delete after Step 0 is decided.
// ============================================================================================
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Cue.Pages;

public sealed partial class ImeSpikePage : Page
{
    private static readonly Regex DateIsh = new(
        @"\d+\s*(?:시|분|일|월|주|시간)|오늘|내일|모레|글피|매주|매일|매월|매년|격주|평일|마다|" +
        @"[월화수목금토일]요일|[월화수목금토일]욜|오전|오후|아침|점심|저녁|새벽|낮|밤",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Color Accent = new() { A = 255, R = 0xE0, G = 0x6C, B = 0x00 };

    private readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly LinkedList<string> _feed = new();   // newest-first, capped
    private const int FeedMax = 14;

    private readonly HashSet<(int Start, int Len)> _painted = new();

    private bool _isComposing;
    private bool _tinting;
    private int _compStart;
    private string _lastText = "";  // last *visible text* we reacted to; ignores format-only re-raises

    // cumulative counters (the freeze/caret signals live here, not in a scrolling log)
    private int _cStart, _cChange, _cEnd;
    private int _tintApplied, _tintSkipped, _undoUnits, _caretJumps, _throws;

    public ImeSpikePage()
    {
        InitializeComponent();

        Box.TextChanged += OnTextChanged;
        Box.TextCompositionStarted += OnCompositionStarted;
        Box.TextCompositionChanged += OnCompositionChanged;
        Box.TextCompositionEnded += OnCompositionEnded;

        _idle.Tick += (_, _) => { _idle.Stop(); Tint("idle"); };

        Feed("ready (v3 dashboard). type Korean above.");
        Refresh();
    }

    // ---- IME composition signal (assumption 1) -------------------------------------------------

    private void OnCompositionStarted(RichEditBox sender, TextCompositionStartedEventArgs args)
    {
        _isComposing = true;
        _cStart++;
        _compStart = Box.Document.Selection.StartPosition;
        Feed($"▶ composition start @ {_compStart}");
        Refresh();
    }

    private void OnCompositionChanged(RichEditBox sender, TextCompositionChangedEventArgs args)
    {
        // Aggregated only — NOT printed per event (this is the firehose we removed).
        _cChange++;
        try { _compStart = args.StartIndex; } catch { /* keep last */ }
        // Default (gate ON): never tint mid-composition — wait for the commit. The IME rewrites the
        // committed syllable on finalize (resetting its format), and touching the active tail risks a
        // leak, so tinting at commit boundaries only is the safe design. The leak-probe (gate OFF)
        // deliberately tints the live composition so the observer can see the bleed.
        if (GateOnNonComposing.IsChecked != true)
            Tint("compose-leak");
        Refresh();
    }

    private void OnCompositionEnded(RichEditBox sender, TextCompositionEndedEventArgs args)
    {
        _isComposing = false;
        _cEnd++;
        _idle.Stop(); // the commit is our tint trigger; don't also let a queued idle tick double it
        Feed("■ composition end → commit");
        Tint("commit");
        _lastText = GetPlainText(); // keep the baseline in sync after a commit
        Refresh();
    }

    // ---- Trigger plumbing ----------------------------------------------------------------------

    private void OnTextChanged(object sender, RoutedEventArgs e)
    {
        if (_tinting) return; // our own re-tint (synchronous case)

        var text = GetPlainText();
        // Applying CharacterFormat can re-raise TextChanged asynchronously, AFTER the _tinting guard
        // has cleared. The visible text is unchanged in that case, so ignore it — this is what kept the
        // dashboard "writing" with no input (idle timer re-armed in a loop). Only react to real edits.
        if (text == _lastText) return;
        _lastText = text;

        // Space (only meaningful when not composing) is a hard delimiter → tint now.
        if (!_isComposing && text.EndsWith(" ", StringComparison.Ordinal))
        {
            _idle.Stop();
            Tint("space");
            return;
        }
        // Otherwise debounce. The idle tick is allowed to fire DURING composition so the final token
        // (e.g. a trailing "3시" whose 시 the IME still holds in composition) lights up after a brief
        // pause without needing a space. By then the syllable is fully formed; the tick paints the
        // whole text including that settled-but-uncommitted tail, and the eventual commit re-tints
        // (full reset) to stay consistent. Per-keystroke tinting stays off, so half-formed syllables
        // mid-typing are never touched.
        _idle.Stop();
        _idle.Start();
    }

    // Paint the whole visible text, including the settled composing tail, so a trailing time token
    // tints on idle. Active-typing safety comes from WHEN we tint (commit boundaries + idle pause,
    // never per-keystroke under the gate), not from clamping the range here.
    private int PaintLimit(string text) => text.Length;

    // ---- Re-tint: FULL RESET then paint matches (assumptions 2 & 3) ----------------------------

    private void Tint(string reason)
    {
        try
        {
            var doc = Box.Document;
            var text = GetPlainText();
            int limit = PaintLimit(text);

            var desired = new List<(int Start, int Len)>();
            foreach (Match m in DateIsh.Matches(text))
                if (m.Index + m.Length <= limit)
                    desired.Add((m.Index, m.Length));

            int caretStart = doc.Selection.StartPosition;
            int caretEnd = doc.Selection.EndPosition;
            // Suspend the TOM undo manager while we format, so the colour never enters the undo stack.
            // Ctrl+Z then only ever sees real text edits, never "uncolour, then delete a char". The
            // toggle lets us A/B against recording-on behaviour.
            bool suspendUndo = IsolateUndoGroup.IsChecked == true;
            const int TomSuspend = -9999983, TomResume = -9999982;

            _tinting = true;
            doc.BatchDisplayUpdates();
            try
            {
                if (suspendUndo) doc.Undo(TomSuspend);
                // FULL RESET to default first, then paint matches. Clears accent that bled into newly
                // typed text and accent the IME wiped when it recommitted a syllable.
                doc.GetRange(0, int.MaxValue).CharacterFormat.ForegroundColor = DefaultColor();
                foreach (var s in desired)
                    doc.GetRange(s.Start, s.Start + s.Len).CharacterFormat.ForegroundColor = Accent;

                doc.Selection.SetRange(caretStart, caretEnd);
                // Reset the insertion (pending) format at the caret so the NEXT typed char never
                // inherits the accent of the run it follows (the brief flash on "회" right after "3시").
                if (caretStart == caretEnd)
                    doc.Selection.CharacterFormat.ForegroundColor = DefaultColor();
            }
            finally
            {
                if (suspendUndo) doc.Undo(TomResume); // resume recording for genuine edits
                doc.ApplyDisplayUpdates();
                _tinting = false;
            }

            _painted.Clear();
            foreach (var s in desired) _painted.Add(s);

            _tintApplied++;
            // With undo suspended, formatting adds ZERO undo units (the Ctrl+Z fix). Without, it grows.
            _undoUnits += suspendUndo ? 0 : (1 + desired.Count);

            bool jumped = doc.Selection.StartPosition != caretStart || doc.Selection.EndPosition != caretEnd;
            if (jumped) _caretJumps++;

            Feed($"tint [{reason}] ={desired.Count}{(jumped ? "  CARET MOVED" : "")}");
        }
        catch (Exception ex)
        {
            _tinting = false;
            _throws++;
            Feed($"tint [{reason}] THREW: {ex.GetType().Name}");
        }
    }

    private Color DefaultColor()
        => ActualTheme == ElementTheme.Dark
            ? new Color { A = 255, R = 0xF2, G = 0xF2, B = 0xF2 }
            : new Color { A = 255, R = 0x1A, G = 0x1A, B = 0x1A };

    // ---- dashboard (cheap) ---------------------------------------------------------------------

    private void Refresh()
    {
        try
        {
            var doc = Box.Document;
            StateNow.Text =
                $"IsComposing={_isComposing}  compStart={_compStart}  caret=({doc.Selection.StartPosition},{doc.Selection.EndPosition})  " +
                $"len={GetPlainText().Length}  painted={_painted.Count}";
            StateCounters.Text =
                $"comp: start {_cStart} / change {_cChange} / end {_cEnd}    " +
                $"tint: applied {_tintApplied} / skipped {_tintSkipped}    undo units {_undoUnits}    caret jumps {_caretJumps}";

            SetVerdict(VComposition, VCompositionText,
                _cStart > 0, $"조합 신호 {(_cStart > 0 ? "✅ 발화" : "… 대기")} ({_cStart})");
            SetVerdict(VCaret, VCaretText,
                _caretJumps == 0, $"caret {(_caretJumps == 0 ? "✅ 안정" : "⚠️ 튐 " + _caretJumps)}");
            // undo units staying small is the good signal; flag if it balloons.
            SetVerdict(VUndo, VUndoText,
                _undoUnits <= 30, $"undo 단위 {_undoUnits}{(_undoUnits <= 30 ? " ✅" : " ⚠️ 폭증")}");
            SetVerdict(VThrow, VThrowText,
                _throws == 0, $"예외 {_throws}{(_throws == 0 ? "" : " ⚠️")}");
        }
        catch { /* never let telemetry throw */ }
    }

    private static void SetVerdict(Border border, TextBlock label, bool ok, string text)
    {
        label.Text = text;
        byte a = 40;
        border.Background = new SolidColorBrush(ok
            ? new Color { A = a, R = 0x2E, G = 0xA0, B = 0x43 }   // green tint
            : new Color { A = a, R = 0xD8, G = 0x3B, B = 0x01 }); // orange/red tint
    }

    // ---- helpers -------------------------------------------------------------------------------

    private string GetPlainText()
    {
        try
        {
            Box.Document.GetText(TextGetOptions.NoHidden, out var s);
            return s.TrimEnd('\r'); // RichEditBox appends a trailing \r
        }
        catch { return string.Empty; }
    }

    private void Feed(string line)
    {
        _feed.AddFirst(line);                 // newest first → no scrolling needed
        while (_feed.Count > FeedMax) _feed.RemoveLast();
        FeedText.Text = string.Join("\n", _feed);
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _feed.Clear();
        FeedText.Text = string.Empty;
        _painted.Clear();
        _cStart = _cChange = _cEnd = 0;
        _tintApplied = _tintSkipped = _undoUnits = _caretJumps = _throws = 0;
        Refresh();
    }
}
