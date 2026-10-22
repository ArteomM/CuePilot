using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace CuePilotDesktop.Views.Controls;

/// <summary>
/// Read-only Markdown for long documents (the parsed files of the knowledge base): the text is parsed in the
/// background and shown block by block in a virtualized list, so a 100-page PDF or a big spreadsheet opens at once
/// and scrolling stays smooth. Colours follow the day/night palette.
/// </summary>
public sealed class MarkdownDocumentView : ItemsControl
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown),
        typeof(string),
        typeof(MarkdownDocumentView),
        new PropertyMetadata(string.Empty, (d, _) => ((MarkdownDocumentView)d).OnMarkdownChanged()));

    private int _version;

    public MarkdownDocumentView()
    {
        Focusable = false;
        FontSize = MarkdownBlockPresenter.BaseFontSize;
        SetResourceReference(ForegroundProperty, "EchoTextBrush");
        VirtualizingPanel.SetIsVirtualizing(this, true);
        VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(this, ScrollUnit.Pixel);
        ScrollViewer.SetCanContentScroll(this, true);

        var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
        scroll.SetValue(ScrollViewer.CanContentScrollProperty, true);
        scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        scroll.SetValue(FocusableProperty, false);
        var presenter = new FrameworkElementFactory(typeof(ItemsPresenter));
        presenter.SetValue(MarginProperty, new Thickness(0, 0, 8, 0));
        scroll.AppendChild(presenter);
        Template = new ControlTemplate(typeof(ItemsControl)) { VisualTree = scroll };
        ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(MarkdownBlockPresenter)) };
    }

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
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
        if (VisualTreeHelper.GetChildrenCount(this) > 0 && VisualTreeHelper.GetChild(this, 0) is ScrollViewer scrollViewer)
        {
            scrollViewer.ScrollToTop();
        }
    }
}

/// <summary>Draws one <see cref="MarkdownBlock"/> with plain TextBlocks and Grids (cheap to create and to recycle).</summary>
public sealed class MarkdownBlockPresenter : Decorator
{
    public const double BaseFontSize = 14.5;

    public MarkdownBlockPresenter()
    {
        DataContextChanged += (_, _) => Child = DataContext is MarkdownBlock block ? Build(block) : null;
    }

    public static UIElement Build(MarkdownBlock block)
    {
        const double size = BaseFontSize;
        switch (block.Kind)
        {
            case MarkdownBlockKind.Heading:
            {
                double headingSize = MarkdownInlines.HeadingSize(size, block.Level);
                TextBlock heading = Text(block.Text, headingSize);
                heading.FontWeight = MarkdownInlines.HeadingWeight(block.Level);
                heading.Margin = new Thickness(0, block.Level <= 2 ? 14 : 10, 0, 6);
                return heading;
            }

            case MarkdownBlockKind.ListItem:
            {
                var grid = new Grid { Margin = new Thickness(block.Level * 18, 1, 0, 4) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var marker = new TextBlock { Text = block.Marker, FontSize = size, LineHeight = size * 1.5 };
                marker.SetResourceReference(TextBlock.ForegroundProperty, "EchoAccentTextBrush");
                TextBlock text = Text(block.Text, size);
                Grid.SetColumn(text, 1);
                grid.Children.Add(marker);
                grid.Children.Add(text);
                return grid;
            }

            case MarkdownBlockKind.Quote:
            {
                TextBlock text = Text(block.Text, size);
                text.SetResourceReference(TextBlock.ForegroundProperty, "EchoMutedBrush");
                var border = new Border { Padding = new Thickness(12, 2, 0, 2), Margin = new Thickness(0, 4, 0, 10), BorderThickness = new Thickness(3, 0, 0, 0), Child = text };
                border.SetResourceReference(Border.BorderBrushProperty, "EchoAccentBrush");
                return border;
            }

            case MarkdownBlockKind.Code:
            {
                var text = new TextBlock { Text = block.Text, FontFamily = new FontFamily("Consolas"), FontSize = size - 1.5, TextWrapping = TextWrapping.Wrap };
                var border = new Border { Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 2, 0, 10), CornerRadius = new CornerRadius(8), Child = text };
                border.SetResourceReference(Border.BackgroundProperty, "EchoSurfaceAltBrush");
                return border;
            }

            case MarkdownBlockKind.Rule:
            {
                var rule = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 10) };
                rule.SetResourceReference(Border.BackgroundProperty, "EchoBorderBrush");
                return rule;
            }

            case MarkdownBlockKind.Note:
            {
                TextBlock note = Text(block.Text, size - 1.5);
                note.FontStyle = FontStyles.Italic;
                note.Margin = new Thickness(0, 4, 0, 8);
                note.SetResourceReference(TextBlock.ForegroundProperty, "EchoMutedBrush");
                return note;
            }

            case MarkdownBlockKind.TableRows:
                return Table(block, size - 1);

            case MarkdownBlockKind.Record:
                return Record(block, size - 1);

            default:
            {
                TextBlock paragraph = Text(block.Text, size);
                paragraph.Margin = new Thickness(0, 0, 0, 10);
                return paragraph;
            }
        }
    }

    private static TextBlock Text(string text, double size)
    {
        var block = new TextBlock { FontSize = size, LineHeight = size * 1.5, TextWrapping = TextWrapping.Wrap };
        MarkdownInlines.AddLines(block.Inlines, text, size);
        return block;
    }

    private static UIElement Table(MarkdownBlock block, double size)
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
            var text = new TextBlock { FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 5, 8, 5) };
            MarkdownInlines.Add(text.Inlines, c < cells.Length ? cells[c] : string.Empty, size);
            if (isHeader)
            {
                text.FontWeight = FontWeights.Bold;
            }

            Grid.SetColumn(text, c);
            grid.Children.Add(text);
        }

        var border = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = grid };
        border.SetResourceReference(Border.BorderBrushProperty, "EchoBorderBrush");
        if (isHeader)
        {
            border.SetResourceReference(Border.BackgroundProperty, "EchoSurfaceAltBrush");
        }

        return border;
    }

    /// <summary>A row of a wide table as «Column: value · Column: value», skipping empty cells.</summary>
    private static UIElement Record(MarkdownBlock block, double size)
    {
        string[] row = block.Rows.Count > 0 ? block.Rows[0] : [];
        var text = new TextBlock { FontSize = size, LineHeight = size * 1.45, TextWrapping = TextWrapping.Wrap };
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
                separator.SetResourceReference(TextElement.ForegroundProperty, "EchoMutedBrush");
                text.Inlines.Add(separator);
            }

            string name = c < block.Header.Length && block.Header[c].Length > 0 ? block.Header[c] : $"#{c + 1}";
            var label = new Run(name + ": ");
            label.SetResourceReference(TextElement.ForegroundProperty, "EchoMutedBrush");
            text.Inlines.Add(label);
            MarkdownInlines.Add(text.Inlines, row[c], size);
            first = false;
        }

        var border = new Border { Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 0, 5), CornerRadius = new CornerRadius(10), Child = text };
        border.SetResourceReference(Border.BackgroundProperty, "EchoSurfaceAltBrush");
        return border;
    }
}
