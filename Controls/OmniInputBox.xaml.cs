using System;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

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

    // Bumped on every real (visible) text change. Step 3 uses it to re-parse at most once per version;
    // declared here so the bridge owns the single source of truth for "the text changed".
    private long _documentVersion;

    public OmniInputBox()
    {
        InitializeComponent();

        Box.TextChanged += OnBoxTextChanged;
        Box.TextCompositionStarted += (_, _) => _isComposing = true;
        Box.TextCompositionEnded += (_, _) => _isComposing = false;
        Box.KeyDown += OnBoxKeyDown;
        Box.Paste += OnBoxPaste;

        Loaded += (_, _) =>
        {
            PushTextToDocument(Text);   // honour a Text set before the template was ready
            UpdatePlaceholder();
        };
    }

    /// <summary>Raised when the user commits the line (Enter while not composing).</summary>
    public event EventHandler? Submit;

    /// <summary>Current monotonically-increasing version of the visible text (Step 3 re-parse dedup).</summary>
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
        self.PushTextToDocument((string)e.NewValue ?? string.Empty);
        self.UpdatePlaceholder();
    }

    // ---- VM <-> Document bridge ----------------------------------------------------------------

    private void OnBoxTextChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingDocument)
            return; // our own SetText — not a user edit

        var text = GetPlainText();
        if (text == (Text ?? string.Empty))
        {
            UpdatePlaceholder();
            return; // formatting-only re-raise or no real change (also breaks any echo loop)
        }

        _documentVersion++;
        _updatingTextFromDocument = true;
        Text = text;                 // propagates to the bound VM property
        _updatingTextFromDocument = false;
        UpdatePlaceholder();
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

    private void UpdatePlaceholder()
        => Placeholder.Visibility = string.IsNullOrEmpty(GetPlainText()) ? Visibility.Visible : Visibility.Collapsed;

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
}
