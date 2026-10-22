using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace CuePilotDesktop.Views.Controls;

/// <summary>**Bold**, `code` and *italic* inside a line, for both TextBlock and FlowDocument inlines.</summary>
public static class MarkdownInlines
{
    // A time limit keeps an odd line (thousands of asterisks) from freezing the window; it is then shown as plain text.
    private static readonly Regex InlinePattern = new(
        @"\*\*(.+?)\*\*|`([^`]+)`|(?<![\*\w])\*(?!\s)(.+?)(?<!\s)\*(?!\*)",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(50));

    public static void Add(InlineCollection inlines, string text, double fontSize)
    {
        if (text.IndexOfAny(['*', '`']) < 0)
        {
            inlines.Add(new Run(text));
            return;
        }

        List<Match> matches;
        try
        {
            matches = InlinePattern.Matches(text).ToList();
        }
        catch (RegexMatchTimeoutException)
        {
            inlines.Add(new Run(text));
            return;
        }

        int position = 0;
        foreach (Match match in matches)
        {
            if (match.Index > position)
            {
                inlines.Add(new Run(text[position..match.Index]));
            }

            if (match.Groups[1].Success)
            {
                inlines.Add(new Bold(new Run(match.Groups[1].Value)));
            }
            else if (match.Groups[2].Success)
            {
                var code = new Run(match.Groups[2].Value) { FontFamily = new FontFamily("Consolas"), FontSize = Math.Max(8, fontSize - 1) };
                code.SetResourceReference(TextElement.BackgroundProperty, "EchoSurfaceAltBrush");
                inlines.Add(code);
            }
            else
            {
                inlines.Add(new Italic(new Run(match.Groups[3].Value)));
            }

            position = match.Index + match.Length;
        }

        if (position < text.Length)
        {
            inlines.Add(new Run(text[position..]));
        }
    }

    /// <summary>Lines of a paragraph separated by line breaks.</summary>
    public static void AddLines(InlineCollection inlines, string text, double fontSize)
    {
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                inlines.Add(new LineBreak());
            }

            Add(inlines, lines[i], fontSize);
        }
    }

    public static double HeadingSize(double fontSize, int level) => level switch
    {
        1 => fontSize + 7,
        2 => fontSize + 3.5,
        3 => fontSize + 1.5,
        _ => fontSize + 0.5
    };

    public static FontWeight HeadingWeight(int level) => level <= 2 ? FontWeights.ExtraBold : FontWeights.Bold;
}
