using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using CuePilotDesktop.Views.Controls;

namespace CuePilot.Avalonia.Views.Controls;

/// <summary>**Bold**, `code` and *italic* inside a line (port of MarkdownInlines.cs).</summary>
public static class MarkdownInlines
{
    // A time limit keeps an odd line (thousands of asterisks) from freezing the window; it is then shown as plain text.
    private static readonly Regex InlinePattern = new(
        @"\*\*(.+?)\*\*|`([^`]+)`|(?<![\*\w])\*(?!\s)(.+?)(?<!\s)\*(?!\*)",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(50));

    public static readonly FontFamily CodeFont = new("Menlo, Consolas, monospace");

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
                inlines.Add(new Run(match.Groups[1].Value) { FontWeight = FontWeight.Bold });
            }
            else if (match.Groups[2].Success)
            {
                var code = new Run(match.Groups[2].Value) { FontFamily = CodeFont, FontSize = Math.Max(8, fontSize - 1) };
                code.Bind(TextElement.BackgroundProperty, code.GetResourceObservable("EchoSurfaceAltBrush"));
                inlines.Add(code);
            }
            else
            {
                inlines.Add(new Run(match.Groups[3].Value) { FontStyle = FontStyle.Italic });
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

    public static FontWeight HeadingWeight(int level) => level <= 2 ? FontWeight.ExtraBold : FontWeight.Bold;
}

/// <summary>Builds one <see cref="MarkdownBlock"/> out of plain text blocks and grids (port of MarkdownBlockPresenter).</summary>
public static class MarkdownBlockBuilder
{
    public const double BaseFontSize = 14.5;

    public static Control Build(MarkdownBlock block, double size = BaseFontSize)
    {
        switch (block.Kind)
        {
            case MarkdownBlockKind.Heading:
            {
                double headingSize = MarkdownInlines.HeadingSize(size, block.Level);
                SelectableTextBlock heading = Text(block.Text, headingSize);
                heading.FontWeight = MarkdownInlines.HeadingWeight(block.Level);
                heading.Margin = new Thickness(0, block.Level <= 2 ? 14 : 10, 0, 6);
                return heading;
            }

            case MarkdownBlockKind.ListItem:
            {
                var grid = new Grid { Margin = new Thickness(block.Level * 18, 1, 0, 4), ColumnDefinitions = new ColumnDefinitions("24,*") };
                var marker = new TextBlock { Text = block.Marker, FontSize = size, LineHeight = size * 1.5 };
                marker.Bind(TextBlock.ForegroundProperty, marker.GetResourceObservable("EchoAccentTextBrush"));
                SelectableTextBlock text = Text(block.Text, size);
                Grid.SetColumn(text, 1);
                grid.Children.Add(marker);
                grid.Children.Add(text);
                return grid;
            }

            case MarkdownBlockKind.Quote:
            {
                SelectableTextBlock text = Text(block.Text, size);
                text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("EchoMutedBrush"));
                var border = new Border { Padding = new Thickness(12, 2, 0, 2), Margin = new Thickness(0, 4, 0, 10), BorderThickness = new Thickness(3, 0, 0, 0), Child = text };
                border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("EchoAccentBrush"));
                return border;
            }

            case MarkdownBlockKind.Code:
            {
                var text = new SelectableTextBlock { Text = block.Text, FontFamily = MarkdownInlines.CodeFont, FontSize = size - 1.5, TextWrapping = TextWrapping.Wrap };
                var border = new Border { Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 2, 0, 10), CornerRadius = new CornerRadius(8), Child = text };
                border.Bind(Border.BackgroundProperty, border.GetResourceObservable("EchoSurfaceAltBrush"));
                return border;
            }

            case MarkdownBlockKind.Rule:
            {
                var rule = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 10) };
                rule.Bind(Border.BackgroundProperty, rule.GetResourceObservable("EchoBorderBrush"));
                return rule;
            }

            case MarkdownBlockKind.Note:
            {
                SelectableTextBlock note = Text(block.Text, size - 1.5);
                note.FontStyle = FontStyle.Italic;
                note.Margin = new Thickness(0, 4, 0, 8);
                note.Bind(TextBlock.ForegroundProperty, note.GetResourceObservable("EchoMutedBrush"));
                return note;
            }

            case MarkdownBlockKind.TableRows:
                return Table(block, size - 1);

            case MarkdownBlockKind.Record:
                return Record(block, size - 1);

            default:
            {
                SelectableTextBlock paragraph = Text(block.Text, size);
                paragraph.Margin = new Thickness(0, 0, 0, 10);
                return paragraph;
            }
        }
    }

    private static SelectableTextBlock Text(string text, double size)
    {
        var block = new SelectableTextBlock { FontSize = size, LineHeight = size * 1.5, TextWrapping = TextWrapping.Wrap };
        block.Inlines ??= [];
        MarkdownInlines.AddLines(block.Inlines, text, size);
        return block;
    }

    private static Control Table(MarkdownBlock block, double size)
    {
        var panel = new StackPanel { Margin = new Thickness(0, block.ShowHeader ? 4 : 0, 0, block.IsLast ? 14 : 0) };
        if (block.ShowHeader)
        {
            panel.Children.Add(TableRow(block.Header, block.ColumnWeights, size, isHeader: true));
        }

        foreach (string[] row in block.Rows)
        {
            panel.Children.Add(TableRow(row, block.ColumnWeights, size, isHeader: false));
        }

        return panel;
    }

    private static Border TableRow(string[] cells, double[] weights, double size, bool isHeader)
    {
        var grid = new Grid();
        for (int c = 0; c < weights.Length; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(weights[c], GridUnitType.Star) });
            var text = new SelectableTextBlock { FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 5, 8, 5) };
            text.Inlines ??= [];
            MarkdownInlines.Add(text.Inlines, c < cells.Length ? cells[c] : string.Empty, size);
            if (isHeader)
            {
                text.FontWeight = FontWeight.Bold;
            }

            Grid.SetColumn(text, c);
            grid.Children.Add(text);
        }

        var border = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = grid };
        border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("EchoBorderBrush"));
        if (isHeader)
        {
            border.Bind(Border.BackgroundProperty, border.GetResourceObservable("EchoSurfaceAltBrush"));
        }

        return border;
    }

    /// <summary>A row of a wide table as «Column: value · Column: value», skipping empty cells.</summary>
    private static Control Record(MarkdownBlock block, double size)
    {
        string[] row = block.Rows.Count > 0 ? block.Rows[0] : [];
        var text = new SelectableTextBlock { FontSize = size, LineHeight = size * 1.45, TextWrapping = TextWrapping.Wrap };
        text.Inlines ??= [];
        bool first = true;
        for (int c = 0; c < row.Length; c++)
        {
            if (row[c].Length == 0)
            {
                continue;
            }

            if (!first)
            {
                var separator = new Run("   ·   ");
                separator.Bind(TextElement.ForegroundProperty, text.GetResourceObservable("EchoMutedBrush"));
                text.Inlines.Add(separator);
            }

            string name = c < block.Header.Length && block.Header[c].Length > 0 ? block.Header[c] : $"#{c + 1}";
            var label = new Run(name + ": ");
            label.Bind(TextElement.ForegroundProperty, text.GetResourceObservable("EchoMutedBrush"));
            text.Inlines.Add(label);
            MarkdownInlines.Add(text.Inlines, row[c], size);
            first = false;
        }

        var border = new Border { Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 0, 5), CornerRadius = new CornerRadius(10), Child = text };
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable("EchoSurfaceAltBrush"));
        return border;
    }
}

/// <summary>
/// Read-only Markdown for long documents (the parsed files of the knowledge base): parsed in the background and shown
/// block by block in a virtualized list, so a 100-page PDF opens at once. Colours follow the day/night palette.
/// </summary>
public sealed class MarkdownDocumentView : ItemsControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownDocumentView, string?>(nameof(Markdown));

    private int _version;
    private ScrollViewer? _scroll;

    static MarkdownDocumentView()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownDocumentView>((view, _) => view.OnMarkdownChanged());
    }

    public MarkdownDocumentView()
    {
        Focusable = false;
        FontSize = MarkdownBlockBuilder.BaseFontSize;
        this.Bind(ForegroundProperty, this.GetResourceObservable("EchoTextBrush"));
        ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
        ItemTemplate = new FuncDataTemplate<MarkdownBlock>((block, _) => block == null ? null : MarkdownBlockBuilder.Build(block));
        Template = new FuncControlTemplate<MarkdownDocumentView>((_, scope) =>
        {
            _scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new ItemsPresenter { Name = "PART_ItemsPresenter", Margin = new Thickness(0, 0, 8, 0) }.RegisterInNameScope(scope)
            };
            return _scroll;
        });
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private async void OnMarkdownChanged()
    {
        int version = ++_version;
        string text = Markdown ?? string.Empty;
        List<MarkdownBlock> blocks = text.Length < 8000
            ? MarkdownBlocks.Parse(text)
            : await Task.Run(() => MarkdownBlocks.Parse(text));
        if (version != _version)
        {
            return;
        }

        ItemsSource = blocks;
        _scroll?.ScrollToHome();
    }
}

/// <summary>
/// Read-only Markdown for answers in the topic dialog: headings, lists, tables, quotes, code and **bold**.
/// Text stays selectable. Long documents use <see cref="MarkdownDocumentView"/>.
/// </summary>
public class MarkdownView : UserControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    /// <summary>Longer texts are cut for display; the full text stays in the knowledge base and is copied whole.</summary>
    public static readonly StyledProperty<int> MaxRenderCharsProperty =
        AvaloniaProperty.Register<MarkdownView, int>(nameof(MaxRenderChars), 30000);

    /// <summary>Disabled inside a chat list: the view grows with its text and the list scrolls.</summary>
    public static readonly StyledProperty<ScrollBarVisibility> VerticalScrollBarVisibilityProperty =
        AvaloniaProperty.Register<MarkdownView, ScrollBarVisibility>(nameof(VerticalScrollBarVisibility), ScrollBarVisibility.Auto);

    private const int MaxTableRows = 80;
    private readonly StackPanel _panel = new();

    static MarkdownView()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.Render());
        VerticalScrollBarVisibilityProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.UpdateHost());
    }

    public MarkdownView()
    {
        FontSize = 14.5;
        this.Bind(ForegroundProperty, this.GetResourceObservable("EchoTextBrush"));
        UpdateHost();
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public int MaxRenderChars
    {
        get => GetValue(MaxRenderCharsProperty);
        set => SetValue(MaxRenderCharsProperty, value);
    }

    public ScrollBarVisibility VerticalScrollBarVisibility
    {
        get => GetValue(VerticalScrollBarVisibilityProperty);
        set => SetValue(VerticalScrollBarVisibilityProperty, value);
    }

    private void UpdateHost()
    {
        Content = null;
        if (_panel.Parent is ScrollViewer old)
        {
            old.Content = null;
        }

        Content = VerticalScrollBarVisibility == ScrollBarVisibility.Disabled
            ? _panel
            : new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = VerticalScrollBarVisibility, Content = _panel };
    }

    private void Render()
    {
        string text = Markdown ?? string.Empty;
        bool truncated = text.Length > MaxRenderChars;
        if (truncated)
        {
            text = text[..MaxRenderChars];
        }

        _panel.Children.Clear();
        _panel.Margin = new Thickness(0, 0, 6, 0);
        int tableRows = 0;
        foreach (MarkdownBlock block in MarkdownBlocks.Parse(text))
        {
            MarkdownBlock shown = block;
            if (block.Kind == MarkdownBlockKind.TableRows)
            {
                // A long table is cut here, the full text stays in the source.
                tableRows = block.ShowHeader ? 0 : tableRows;
                int room = Math.Max(0, MaxTableRows - tableRows);
                tableRows += block.Rows.Count;
                if (room < block.Rows.Count)
                {
                    shown = new MarkdownBlock
                    {
                        Kind = block.Kind,
                        Header = block.Header,
                        Rows = block.Rows.Take(room).ToList(),
                        ColumnWeights = block.ColumnWeights,
                        ShowHeader = block.ShowHeader,
                        IsLast = block.IsLast
                    };
                }

                if (shown.Rows.Count > 0 || shown.ShowHeader)
                {
                    _panel.Children.Add(MarkdownBlockBuilder.Build(shown, FontSize));
                }

                if (block.IsLast && tableRows > MaxTableRows)
                {
                    _panel.Children.Add(Muted($"… +{tableRows - MaxTableRows}"));
                }

                continue;
            }

            _panel.Children.Add(MarkdownBlockBuilder.Build(shown, FontSize));
        }

        if (truncated)
        {
            _panel.Children.Add(Muted("…"));
        }
    }

    private static TextBlock Muted(string text)
    {
        var block = new TextBlock { Text = text, Margin = new Thickness(0, 4, 0, 8) };
        block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable("EchoMutedBrush"));
        return block;
    }
}
