using System;
using System.Collections.Generic;
using Cue.Parsing;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
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

    /// <summary>Raised when the user commits the line (Enter while not composing).</summary>
    public event EventHandler? Submit;

    /// <summary>Supplies the recognized tokens for the raw line (the VM parses at the current clock/zone).
    /// Positional only; null means "no tinting".</summary>
    public Func<string, IReadOnlyList<QuickAddToken>>? Tokenizer { get; set; }

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
        if (GetPlainText() == (text ?? string.Empty))
            return; // already in sync; avoid a needless caret reset

        try
        {
            _syncingDocument = true;
            Box.Document.SetText(TextSetOptions.None, text ?? string.Empty);
        }
        finally
        {
            _syncingDocument = false;
        }
        _documentVersion++; // external (VM) set changed the visible text too
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
        Submit?.Invoke(this, EventArgs.Empty);
    }

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
        try { tokens = Tokenizer?.Invoke(raw) ?? Array.Empty<QuickAddToken>(); }
        catch { tokens = Array.Empty<QuickAddToken>(); }

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
