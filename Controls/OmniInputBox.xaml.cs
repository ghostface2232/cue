using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Cue.Parsing;
using Cue.ViewModels;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Cue.Controls;

/// <summary>
/// The quick-add input box. Step 2 scope: a <see cref="RichEditBox"/> (the physical prerequisite for
/// per-token accent later) wired to the view model through a manual text bridge, with an IME
/// composition gate and the same chrome/commit behaviour as the old quick-add TextBox. No accent
/// tinting yet — that is Step 3, layered on inside this control.
/// </summary>
public sealed partial class OmniInputBox : UserControl
{
    // Guards so the two-way bridge can't loop: VM -> DP -> Document and Document -> DP -> VM.
    private bool _syncingDocument;            // we are writing the document; ignore the TextChanged it raises
    private bool _updatingTextFromDocument;   // we are writing the Text DP from the document; don't write back

    // IME composition state — set from the composition events. While true we never reformat (Step 3) and
    // never treat Enter as a task commit (the IME owns that keystroke to finalize the syllable).
    private bool _isComposing;

    // Bumped on every real (visible) text change; re-tint re-parses at most once per version.
    private long _documentVersion;
    private long _lastTintedVersion = -1;

    // Editor-held revert (suppression) state (plan §4/§5). The parser is stateless, so reverted spans live
    // here: they're excluded from recognition (no token, no re-tint) yet kept in the title at commit. We
    // reproject them across each edit (single contiguous delta) so they track the word they pin.
    private readonly List<TextSpan> _suppressed = new();
    // The last visible text we observed — the "old" coordinate space for reprojecting _suppressed.
    private string _lastText = string.Empty;

    // The tokens painted by the last re-tint, in original-text coordinates — what a tap hit-tests against
    // to open the correct/revert popover. Stays aligned with the document because re-tint repaints it.
    private IReadOnlyList<QuickAddToken> _tokens = Array.Empty<QuickAddToken>();

    // Re-tint after a typing pause for edits that don't go through composition (paste, delete, arrows,
    // digits/latin). Composition input is covered by TextCompositionEnded instead.
    private readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private UISettings? _uiSettings;

    public OmniInputBox()
    {
        InitializeComponent();

        Box.TextChanged += OnBoxTextChanged;
        Box.TextCompositionStarted += (_, _) => _isComposing = true;
        Box.TextCompositionEnded += OnBoxCompositionEnded;
        // PreviewKeyDown (tunneling) — RichEditBox inserts the newline in its own KeyDown handling, so a
        // bubbling KeyDown handler is too late; we must intercept Enter on the way down.
        Box.PreviewKeyDown += OnBoxKeyDown;
        Box.Paste += OnBoxPaste;
        // RichEditBox handles pointer input internally and marks Tapped handled, so a plain `Box.Tapped +=`
        // never fires — register with handledEventsToo so the token correct/revert popover still gets the tap.
        Box.AddHandler(UIElement.TappedEvent, new TappedEventHandler(OnBoxTapped), handledEventsToo: true);

        _idle.Tick += (_, _) => { _idle.Stop(); if (!_isComposing) ReTint(); };
        // CharacterFormat does not auto-flip with the theme — re-apply the accent/default on theme change.
        ActualThemeChanged += (_, _) => ReTint(force: true);

        Loaded += (_, _) =>
        {
            PushTextToDocument(Text);   // honour a Text set before the template was ready
            UpdatePlaceholder();
            ReTint();
        };
    }

    /// <summary>Raised when the user commits the line (Enter while not composing). Carries the raw text and
    /// the editor-held reverts so the VM re-parses at the current clock honouring them (plan §5.2).</summary>
    public event EventHandler<QuickAddSubmission>? Submit;

    /// <summary>Supplies the recognized tokens for the raw line, given the spans the user has reverted (so a
    /// reverted word emits no token). The VM parses at the current clock/zone. Null means "no tinting".</summary>
    public Func<string, IReadOnlyList<TextSpan>, IReadOnlyList<QuickAddToken>>? Tokenizer { get; set; }

    /// <summary>Supplies the resolved date/time of the live line for the token popover header (so the user
    /// sees "내일 → 6월 30일 (화)"). Honours the same reverts. Null/throwing degrades to a header without it.</summary>
    public Func<string, IReadOnlyList<TextSpan>, QuickAddPreview>? PreviewProvider { get; set; }

    /// <summary>Current monotonically-increasing version of the visible text (re-parse dedup).</summary>
    public long DocumentVersion => _documentVersion;

    // ---- Dependency properties (bridged to the VM via x:Bind) ----------------------------------

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(OmniInputBox), new PropertyMetadata(string.Empty, OnTextPropertyChanged));

    /// <summary>The plain text, two-way bound to the VM's quick-add text.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty PlaceholderNameProperty = DependencyProperty.Register(
        nameof(PlaceholderName), typeof(string), typeof(OmniInputBox), new PropertyMetadata(string.Empty));

    /// <summary>Accent-colored prefix of the placeholder (e.g. a group/tag name).</summary>
    public string PlaceholderName
    {
        get => (string)GetValue(PlaceholderNameProperty);
        set => SetValue(PlaceholderNameProperty, value);
    }

    public static readonly DependencyProperty PlaceholderSuffixProperty = DependencyProperty.Register(
        nameof(PlaceholderSuffix), typeof(string), typeof(OmniInputBox), new PropertyMetadata(string.Empty));

    /// <summary>Normal-colored remainder of the placeholder (e.g. "할 일 입력하기").</summary>
    public string PlaceholderSuffix
    {
        get => (string)GetValue(PlaceholderSuffixProperty);
        set => SetValue(PlaceholderSuffixProperty, value);
    }

    private static void OnTextPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (OmniInputBox)d;
        if (self._updatingTextFromDocument)
            return; // change originated in the document — don't echo it back and reset the caret
        var value = (string)e.NewValue ?? string.Empty;
        self.PushTextToDocument(value);
        self.UpdatePlaceholder(value);
        // Re-tint off the document's own (trimmed) text so token offsets match the painted range exactly —
        // the VM set is not the hot keystroke path, so the extra read here is fine.
        self.ReTint();
    }

    // ---- VM <-> Document bridge ----------------------------------------------------------------

    private void OnBoxTextChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingDocument)
            return; // our own SetText — not a user edit

        var text = GetPlainText(); // single COM read per change; threaded into placeholder + re-tint below
        if (text == (Text ?? string.Empty))
        {
            UpdatePlaceholder(text);
            return; // formatting-only re-raise or no real change (also breaks any echo loop)
        }

        ReprojectSuppressions(text); // move the editor-held reverts across this edit before bumping version
        _documentVersion++;
        _updatingTextFromDocument = true;
        Text = text;                 // propagates to the bound VM property
        _updatingTextFromDocument = false;
        UpdatePlaceholder(text);

        // Re-tint trigger (unified rules): never while composing (that path re-tints on
        // TextCompositionEnded); space is a hard delimiter → now; otherwise debounce via idle.
        _idle.Stop();
        if (_isComposing)
            return;
        if (text.EndsWith(" ", StringComparison.Ordinal))
            ReTint(text, force: false);
        else
            _idle.Start();
    }

    private void PushTextToDocument(string text)
    {
        var value = text ?? string.Empty;
        if (GetPlainText() == value)
            return; // already in sync; avoid a needless caret reset

        try
        {
            _syncingDocument = true;
            Box.Document.SetText(TextSetOptions.None, value);
        }
        finally
        {
            _syncingDocument = false;
        }
        // The IME gate / TextChanged path is bypassed for our own SetText, so reproject here. A commit
        // clears the line (value == "") → the edit covers the whole text → every revert is dropped.
        ReprojectSuppressions(value);
        _documentVersion++; // external (VM) set changed the visible text too
    }

    /// <summary>Moves the editor-held reverts from the previous text to <paramref name="newText"/> and
    /// records it as the new "old" coordinate space. A revert whose own word the edit touched is dropped by
    /// the tracker (the user edited that word). Pure; never throws.</summary>
    private void ReprojectSuppressions(string newText)
    {
        if (_suppressed.Count > 0 && !string.Equals(_lastText, newText, StringComparison.Ordinal))
        {
            var moved = SuppressionTracker.Reproject(_suppressed, _lastText, newText);
            _suppressed.Clear();
            _suppressed.AddRange(moved);
        }
        _lastText = newText;
    }

    private string GetPlainText()
    {
        try
        {
            Box.Document.GetText(TextGetOptions.NoHidden, out var s);
            return s.TrimEnd('\r'); // RichEditBox appends a trailing paragraph mark — strip it here, at the bridge boundary
        }
        catch
        {
            return string.Empty;
        }
    }

    private void UpdatePlaceholder() => UpdatePlaceholder(GetPlainText());

    private void UpdatePlaceholder(string text)
        => Placeholder.Visibility = string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;

    // ---- IME-safe Enter + plain-text paste -----------------------------------------------------

    private void OnBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;

        // Enter while composing finalizes the IME syllable only — it must not commit the task (and the
        // platform usually consumes this keystroke for the IME anyway). The next Enter commits.
        if (_isComposing)
            return;

        e.Handled = true; // also prevents a newline being inserted into the single-line box
        Submit?.Invoke(this, BuildSubmission());
    }

    /// <summary>Snapshots the raw line + editor-held reverts for the VM to re-parse at the current clock.</summary>
    private QuickAddSubmission BuildSubmission()
        => new(GetPlainText(), _suppressed.ToArray(), _documentVersion);

    private async void OnBoxPaste(object sender, TextControlPasteEventArgs e)
    {
        // Force plain-text paste so external formatting (RTF/HTML) can't leak into the box.
        e.Handled = true;
        try
        {
            var view = Clipboard.GetContent();
            if (view.Contains(StandardDataFormats.Text))
            {
                var text = await view.GetTextAsync();
                Box.Document.Selection.SetText(TextSetOptions.None, text ?? string.Empty);
                Box.Document.Selection.Collapse(false); // caret to end of the inserted text
            }
        }
        catch
        {
            // Clipboard can throw (access denied / odd payloads); a failed paste must not crash the box.
        }
    }

    private void OnBoxCompositionEnded(RichEditBox sender, TextCompositionEndedEventArgs args)
    {
        _isComposing = false;
        _idle.Stop();   // the commit is our trigger; don't let a queued idle double it
        ReTint();       // a syllable just finalized — paint the now-committed text
    }

    // ---- Click-to-correct popover (Step 5): tap a token → alternatives / revert -------------------

    private void OnBoxTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_isComposing)
            return; // don't interrupt an in-flight syllable

        // The tap has already moved the caret to the clicked character; read it instead of doing our own
        // point→index hit-test (no GetRangeFromPoint coordinate-space guessing). Map the caret to a token.
        int caret;
        try { caret = Box.Document.Selection.StartPosition; }
        catch { return; }

        var token = HitToken(caret);
        if (token is not null)
            ShowTokenFlyout(token, e.GetPosition(Box));
    }

    /// <summary>The painted token under <paramref name="caret"/>, or null. Prefers a strictly-interior hit;
    /// falls back to the caret resting just after a token (clicking its trailing edge).</summary>
    private QuickAddToken? HitToken(int caret)
    {
        foreach (var t in _tokens)
            if (t.Length > 0 && caret >= t.Start && caret < t.Start + t.Length)
                return t;
        foreach (var t in _tokens)
            if (t.Length > 0 && caret == t.Start + t.Length)
                return t;
        return null;
    }

    private void ShowTokenFlyout(QuickAddToken token, Point at)
    {
        var preview = SafePreview();

        // Drop any preset identical to what's already there (a no-op swap — e.g. "이번 주 금요일" on a bare
        // "금요일" token, since both replace to the same text).
        var alts = AlternativesFor(token, preview)
            .Where(a => !string.Equals(a.Text, token.Text, StringComparison.Ordinal))
            .ToList();

        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };

        // Non-interactive header: icon + original → interpreted, so the user confirms what the parser made
        // of this span before correcting it (disabled = the conventional muted menu header).
        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = HeaderText(token, preview),
            Icon = new FontIcon { Glyph = GlyphFor(token.Kind) },
            IsEnabled = false,
        });
        flyout.Items.Add(new MenuFlyoutSeparator());

        foreach (var (label, replacement) in alts)
        {
            var item = new MenuFlyoutItem { Text = label };
            var rep = replacement; // capture per iteration
            item.Click += (_, _) => ReplaceToken(token, rep);
            flyout.Items.Add(item);
        }
        if (alts.Count > 0)
            flyout.Items.Add(new MenuFlyoutSeparator());

        // The always-present seat: undo the recognition. Registers a suppression so the parser stops
        // recognizing this span — it stays in the title and won't be re-sucked into a date (plan §5).
        var revert = new MenuFlyoutItem { Text = "원문으로 되돌리기" };
        revert.Click += (_, _) => RevertToken(token);
        flyout.Items.Add(revert);

        flyout.ShowAt(Box, new FlyoutShowOptions { Position = at });
    }

    private QuickAddPreview SafePreview()
    {
        try { return PreviewProvider?.Invoke(GetPlainText(), _suppressed) ?? EmptyPreview; }
        catch { return EmptyPreview; }
    }

    private static readonly QuickAddPreview EmptyPreview = new(null, null, null, null);

    /// <summary>The header line "원문 → 해석". Date/time show the resolved value (from the preview provider);
    /// recurrence reuses its own self-describing phrase; someday reads "날짜 없음".</summary>
    private static string HeaderText(QuickAddToken token, QuickAddPreview preview) => token.Kind switch
    {
        QuickAddTokenKind.Date => preview.DateText is { } d ? $"{token.Text} → {d}" : token.Text,
        QuickAddTokenKind.Time => preview.TimeText is { } t ? $"{token.Text} → {t}" : token.Text,
        QuickAddTokenKind.Someday => $"{token.Text} → 날짜 없음",
        _ => token.Text, // Recurrence: the phrase ("매주 금요일") already reads as the rule
    };

    private static string GlyphFor(QuickAddTokenKind kind) => kind switch
    {
        QuickAddTokenKind.Date => "",       // Calendar
        QuickAddTokenKind.Time => "",       // Clock
        QuickAddTokenKind.Recurrence => "",  // RepeatAll
        _ => "",                            // History (someday)
    };

    /// <summary>The quick alternatives for a token, as (menu label, replacement text) — every replacement is
    /// verified to re-parse (QuickAddPresetTests). A weekday date token gets adjacent-weekday + week shifts;
    /// any other date token gets the generic presets. A time token gets presets plus ±1 hour / AM-PM flip
    /// computed from the resolved clock. Recurrence keeps the weekday across a frequency swap.</summary>
    private static IReadOnlyList<(string Label, string Text)> AlternativesFor(QuickAddToken token, QuickAddPreview preview) => token.Kind switch
    {
        QuickAddTokenKind.Date when WeekdayChar.Match(token.Text) is { Success: true } m => WeekdayDateAlternatives(token.Text, m.Value[0]),
        QuickAddTokenKind.Date => new[] { ("오늘", "오늘"), ("내일", "내일"), ("모레", "모레"), ("이번 주말", "이번 주말"), ("다음 주", "다음 주") },
        QuickAddTokenKind.Time => TimeAlternatives(preview),
        QuickAddTokenKind.Recurrence => RecurrenceAlternatives(token.Text),
        _ => Array.Empty<(string, string)>(),
    };

    private const string WeekOrder = "월화수목금토일";
    private static readonly Regex WeekdayChar = new(@"[월화수목금토일](?=요일|욜)", RegexOptions.Compiled);

    /// <summary>Weekday-date alternatives, aware of which week the token already denotes (이번/다음/다다음
    /// 주). Offers the adjacent weekdays (전/후) <i>in the same week</i>, then the same weekday in the two
    /// <i>other</i> weeks (so the current week isn't re-suggested). "이번 주" replaces to a bare "{요일}" —
    /// the parser doesn't consume a literal "이번 주" prefix, and bare already means the upcoming one.</summary>
    private static IReadOnlyList<(string Label, string Text)> WeekdayDateAlternatives(string tokenText, char wd)
    {
        var week = WeekOffset(tokenText);
        var d = WeekOrder.IndexOf(wd);

        var list = new List<(string, string)>();
        AddDayNeighbor(list, "하루 전", week, d - 1); // the calendar day before
        AddDayNeighbor(list, "하루 뒤", week, d + 1); // the calendar day after
        for (var w = 0; w <= 2; w++)
            if (w != week)
                list.Add(WeekShiftOption(w, wd)); // same weekday, the other two weeks
        return list;
    }

    /// <summary>Adds the calendar-day neighbor (±1 day) as a week-aware weekday option, prefixing the label
    /// with 하루 전/하루 뒤 so the direction reads unambiguously next to "다음 주 목/토요일". Crossing the
    /// Mon/Sun boundary shifts the week; a neighbor that would land in the past (before 이번 주) or beyond
    /// 다다음 주 is skipped rather than mislabeled — a weekday word only ever points forward.</summary>
    private static void AddDayNeighbor(List<(string Label, string Text)> list, string prefix, int week, int dayIndex)
    {
        var w = week;
        if (dayIndex < 0) { dayIndex += 7; w--; }      // before Monday → previous week's Sunday
        else if (dayIndex > 6) { dayIndex -= 7; w++; } // after Sunday → next week's Monday
        if (w is < 0 or > 2)
            return;
        var text = WeekdayPhrase(w, WeekOrder[dayIndex]);
        list.Add(($"{prefix} · {text}", text));
    }

    /// <summary>Which week the token's weekday phrase denotes: 2 = 다다음 주, 1 = 다음/담(주), 0 = 이번 주 (a
    /// bare weekday). "다다음" must be tested before "다음" since it contains it.</summary>
    private static int WeekOffset(string tokenText)
        => tokenText.Contains("다다음", StringComparison.Ordinal) ? 2
            : tokenText.Contains("다음", StringComparison.Ordinal) || tokenText.Contains("담", StringComparison.Ordinal) ? 1
            : 0;

    private static string WeekdayPhrase(int week, char wd) => week switch
    {
        1 => $"다음 주 {wd}요일",
        2 => $"다다음 주 {wd}요일",
        _ => $"{wd}요일", // this week = bare (the upcoming one)
    };

    /// <summary>A same-weekday option for week <paramref name="week"/>. The label always names the week; the
    /// 이번 주 replacement is the bare weekday (the parser leaks a literal "이번 주" into the title).</summary>
    private static (string Label, string Text) WeekShiftOption(int week, char wd)
    {
        var text = WeekdayPhrase(week, wd);
        var label = week == 0 ? $"이번 주 {wd}요일" : text;
        return (label, text);
    }

    /// <summary>Time alternatives, computed from the resolved clock: ±1 hour and an AM/PM flip, formatted as
    /// explicit 오전/오후 times so they re-parse unambiguously. Empty when the line resolved no time (then the
    /// popover shows the header + revert only).</summary>
    private static IReadOnlyList<(string Label, string Text)> TimeAlternatives(QuickAddPreview preview)
    {
        if (preview is not { Hour: int h, Minute: int min })
            return Array.Empty<(string, string)>();

        var earlier = ClockText((h + 23) % 24, min);
        var later = ClockText((h + 1) % 24, min);
        var flipped = ClockText((h + 12) % 24, min);
        return new[]
        {
            ($"1시간 일찍 · {earlier}", earlier),
            ($"1시간 늦게 · {later}", later),
            ((h >= 12 ? "오전으로" : "오후로") + $" · {flipped}", flipped),
        };
    }

    /// <summary>Formats a 24-hour clock as an explicit Korean time ("오후 2시", "오전 3시 30분") — the
    /// unambiguous form, so the replace-and-reparse round-trip can't be re-read as a different hour.</summary>
    private static string ClockText(int hour24, int minute)
    {
        var (meridiem, h12) = hour24 == 0 ? ("오전", 12)
            : hour24 < 12 ? ("오전", hour24)
            : hour24 == 12 ? ("오후", 12)
            : ("오후", hour24 - 12);
        return minute == 0 ? $"{meridiem} {h12}시" : $"{meridiem} {h12}시 {minute}분";
    }

    /// <summary>Recurrence alternatives. 매일/평일 are universal frequency swaps; when the phrase names a
    /// weekday we also offer 매주/격주 of that same weekday (a weekly↔biweekly toggle that keeps the day).</summary>
    private static IReadOnlyList<(string Label, string Text)> RecurrenceAlternatives(string tokenText)
    {
        var list = new List<(string, string)> { ("매일", "매일"), ("평일", "평일") };
        var m = WeekdayChar.Match(tokenText);
        if (m.Success)
        {
            var wd = $"{m.Value}요일"; // normalize 금욜 → 금요일
            list.Add(($"매주 {wd}", $"매주 {wd}"));
            list.Add(($"격주 {wd}", $"격주 {wd}"));
        }
        return list;
    }

    /// <summary>Replaces the token's span with <paramref name="replacement"/>. A real edit: it flows through
    /// the text bridge (VM update + suppression reprojection), then we force an immediate re-tint.</summary>
    private void ReplaceToken(QuickAddToken token, string replacement)
    {
        try
        {
            var doc = Box.Document;
            doc.GetRange(token.Start, token.Start + token.Length).SetText(TextSetOptions.None, replacement);
            var caret = token.Start + replacement.Length;
            doc.Selection.SetRange(caret, caret);
            ReTint(force: true);
            Box.Focus(FocusState.Programmatic);
        }
        catch
        {
            // A position/format failure must not crash the box — leave the text as-is.
        }
    }

    /// <summary>Reverts the token: register its span as suppressed (excluded from recognition, kept in the
    /// title) and repaint so its accent drops. The span rides subsequent edits via reprojection.</summary>
    private void RevertToken(QuickAddToken token)
    {
        _suppressed.Add(new TextSpan(token.Start, token.Length));
        ReTint(force: true);
        Box.Focus(FocusState.Programmatic);
    }

    // ---- Inline accent (Step 3): full reset, then paint token spans one accent colour --------------

    private void ReTint(bool force = false) => ReTint(GetPlainText(), force);

    private void ReTint(string raw, bool force)
    {
        if (_isComposing)
            return; // never format a composing (incomplete) syllable
        if (!force && _lastTintedVersion == _documentVersion)
            return; // already tinted this exact text — collapses space+CompositionEnded+idle into one parse
        _lastTintedVersion = _documentVersion;

        IReadOnlyList<QuickAddToken> tokens;
        try { tokens = Tokenizer?.Invoke(raw, _suppressed) ?? Array.Empty<QuickAddToken>(); }
        catch { tokens = Array.Empty<QuickAddToken>(); }
        _tokens = tokens; // keep the tap hit-test aligned with what we paint

        try
        {
            var doc = Box.Document;
            var caretStart = doc.Selection.StartPosition;
            var caretEnd = doc.Selection.EndPosition;
            var accent = AccentColor();
            var normal = DefaultColor();

            _syncingDocument = true; // formatting must not be read back as a user edit
            doc.BatchDisplayUpdates();
            try
            {
                // Full reset clears any bled/IME-recommitted accent, then paint only the current tokens.
                doc.GetRange(0, int.MaxValue).CharacterFormat.ForegroundColor = normal;
                foreach (var t in tokens)
                {
                    if (t.Length <= 0)
                        continue;
                    doc.GetRange(t.Start, t.Start + t.Length).CharacterFormat.ForegroundColor = accent;
                }
                doc.Selection.SetRange(caretStart, caretEnd);
                if (caretStart == caretEnd)
                    doc.Selection.CharacterFormat.ForegroundColor = normal; // next typed char won't inherit accent
            }
            finally
            {
                doc.ApplyDisplayUpdates();
                _syncingDocument = false;
            }
        }
        catch
        {
            _syncingDocument = false; // a position/format failure degrades to "no tint" — never throws
        }
    }

    private Color AccentColor()
    {
        try
        {
            _uiSettings ??= new UISettings();
            return _uiSettings.GetColorValue(UIColorType.Accent);
        }
        catch
        {
            return new Color { A = 255, R = 0x4C, G = 0x8B, B = 0xF5 };
        }
    }

    private Color DefaultColor()
        => ActualTheme == ElementTheme.Dark
            ? new Color { A = 255, R = 0xF2, G = 0xF2, B = 0xF2 }
            : new Color { A = 255, R = 0x1A, G = 0x1A, B = 0x1A };
}
