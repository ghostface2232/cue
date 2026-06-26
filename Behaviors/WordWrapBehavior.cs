using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cue.Behaviors;

/// <summary>
/// Provides helper attached properties to enable clean word-wrapping (CSS keep-all behavior)
/// on CJK (Korean) text, preventing syllables within a word from being split across lines.
/// </summary>
public static class WordWrapBehavior
{
    public static readonly DependencyProperty KeepAllTextProperty =
        DependencyProperty.RegisterAttached(
            "KeepAllText",
            typeof(string),
            typeof(WordWrapBehavior),
            new PropertyMetadata(null, OnKeepAllTextChanged));

    public static string GetKeepAllText(DependencyObject obj) => (string)obj.GetValue(KeepAllTextProperty);
    public static void SetKeepAllText(DependencyObject obj, string value) => obj.SetValue(KeepAllTextProperty, value);

    private static void OnKeepAllTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBlock textBlock)
        {
            var text = e.NewValue as string;
            if (string.IsNullOrEmpty(text))
            {
                textBlock.Text = string.Empty;
            }
            else
            {
                // Split by spaces, insert Word Joiner (U+2060) between characters of each word to prevent mid-word breaking,
                // and rejoin the words with normal spaces so the layout engine only breaks lines at spaces.
                var words = text.Split(' ');
                for (int i = 0; i < words.Length; i++)
                {
                    if (words[i].Length > 1)
                    {
                        words[i] = string.Join("\u2060", words[i].ToCharArray());
                    }
                }
                textBlock.Text = string.Join(" ", words);
            }
        }
    }
}
