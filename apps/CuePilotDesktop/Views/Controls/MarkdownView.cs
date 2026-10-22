using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace CuePilotDesktop.Views.Controls;

/// <summary>
/// Read-only Markdown for answers in the topic dialog: headings, lists, tables, quotes, code and **bold**.
/// Text stays selectable; colours follow the day/night palette. Long documents use <see cref="MarkdownDocumentView"/>.
/// </summary>
public class MarkdownView : RichTextBox
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown),
        typeof(string),
        typeof(MarkdownView),
        new PropertyMetadata(string.Empty, (d, _) => ((MarkdownView)d).Render()));

    /// <summary>Longer texts are cut for display; the full text stays in the knowledge base and is copied whole.</summary>
    public static readonly DependencyProperty MaxRenderCharsProperty = DependencyProperty.Register(
        nameof(MaxRenderChars),
        typeof(int),
        typeof(MarkdownView),
        new PropertyMetadata(30000));

    public MarkdownView()
    {
        IsReadOnly = true;
        IsDocumentEnabled = true;
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        Padding = new Thickness(0);
        FontSize = 14.5;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        SetResourceReference(ForegroundProperty, "EchoTextBrush");
        SetResourceReference(CaretBrushProperty, "EchoTextBrush");
        Document = new FlowDocument();
    }

    /// <summary>Inside a chat list the view does not scroll by itself: the wheel goes to the list.</summary>
    protected override void OnPreviewMouseWheel(System.Windows.Input.MouseWheelEventArgs e)
    {
        if (VerticalScrollBarVisibility == ScrollBarVisibility.Disabled && !e.Handled)
        {
            e.Handled = true;
            var forwarded = new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = MouseWheelEvent,
                Source = this
            };
            (VisualTreeHelper.GetParent(this) as UIElement)?.RaiseEvent(forwarded);
            return;
        }

        base.OnPreviewMouseWheel(e);
    }

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public int MaxRenderChars
    {
        get => (int)GetValue(MaxRenderCharsProperty);
        set => SetValue(MaxRenderCharsProperty, value);
    }

    private void Render()
    {
        string text = Markdown ?? string.Empty;
        bool truncated = text.Length > MaxRenderChars;
        if (truncated)
        {
            text = text[..MaxRenderChars];
        }

        var document = new FlowDocument
        {
            PagePadding = new Thickness(0, 0, 6, 0),
            FontFamily = FontFamily,
            FontSize = FontSize,
            LineHeight = FontSize * 1.5
        };
        document.SetResourceReference(FlowDocument.ForegroundProperty, "EchoTextBrush");

        List? list = null;
        string listKind = string.Empty;
        int tableRows = 0;
        Table? table = null;
        foreach (MarkdownBlock block in MarkdownBlocks.Parse(text))
        {
            if (block.Kind != MarkdownBlockKind.ListItem)
            {
                list = null;
            }

            if (block.Kind != MarkdownBlockKind.TableRows || block.ShowHeader)
            {
                table = null;
            }

            switch (block.Kind)
            {
                case MarkdownBlockKind.Heading:
                    var heading = new Paragraph
                    {
                        FontSize = MarkdownInlines.HeadingSize(FontSize, block.Level),
                        FontWeight = MarkdownInlines.HeadingWeight(block.Level),
                        Margin = new Thickness(0, block.Level <= 2 ? 12 : 8, 0, 6)
                    };
                    MarkdownInlines.Add(heading.Inlines, block.Text, FontSize);
                    document.Blocks.Add(heading);
                    break;
                case MarkdownBlockKind.ListItem:
                    string kind = block.Marker == "•" ? "bullet" : "ordered";
                    if (list == null || listKind != kind)
                    {
                        list = new List
                        {
                            MarkerStyle = kind == "bullet" ? TextMarkerStyle.Disc : TextMarkerStyle.Decimal,
                            Margin = new Thickness(0, 2, 0, 8),
                            Padding = new Thickness(22, 0, 0, 0)
                        };
                        if (kind == "ordered" && int.TryParse(block.Marker.TrimEnd('.'), out int start))
                        {
                            list.StartIndex = start;
                        }

                        listKind = kind;
                        document.Blocks.Add(list);
                    }

                    var item = new Paragraph { Margin = new Thickness(0, 1, 0, 3) };
                    MarkdownInlines.Add(item.Inlines, block.Text, FontSize);
                    list.ListItems.Add(new ListItem(item));
                    break;
                case MarkdownBlockKind.Quote:
                    var quote = new Paragraph { Margin = new Thickness(0, 4, 0, 8), Padding = new Thickness(12, 2, 0, 2), BorderThickness = new Thickness(3, 0, 0, 0) };
                    quote.SetResourceReference(Block.BorderBrushProperty, "EchoAccentBrush");
                    quote.SetResourceReference(TextElement.ForegroundProperty, "EchoMutedBrush");
                    MarkdownInlines.Add(quote.Inlines, block.Text, FontSize);
                    document.Blocks.Add(quote);
                    break;
                case MarkdownBlockKind.Code:
                    var code = new Paragraph(new Run(block.Text))
                    {
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = FontSize - 1.5,
                        Padding = new Thickness(12, 8, 12, 8),
                        Margin = new Thickness(0, 2, 0, 10)
                    };
                    code.SetResourceReference(Block.BackgroundProperty, "EchoSurfaceAltBrush");
                    document.Blocks.Add(code);
                    break;
                case MarkdownBlockKind.Rule:
                    var rule = new Paragraph { Margin = new Thickness(0, 6, 0, 6), FontSize = 2, BorderThickness = new Thickness(0, 0, 0, 1) };
                    rule.SetResourceReference(Block.BorderBrushProperty, "EchoBorderBrush");
                    document.Blocks.Add(rule);
                    break;
                case MarkdownBlockKind.Note:
                    var note = new Paragraph { Margin = new Thickness(0, 2, 0, 8), FontStyle = FontStyles.Italic, FontSize = FontSize - 1.5 };
                    note.SetResourceReference(TextElement.ForegroundProperty, "EchoMutedBrush");
                    MarkdownInlines.Add(note.Inlines, block.Text, FontSize);
                    document.Blocks.Add(note);
                    break;
                case MarkdownBlockKind.TableRows:
                    if (table == null)
                    {
                        table = NewTable(block);
                        tableRows = 0;
                        document.Blocks.Add(table);
                    }

                    // A rich text box lays out every cell at once: long tables are cut here, the full text stays in the source.
                    foreach (string[] row in block.Rows)
                    {
                        if (tableRows++ < MaxTableRows)
                        {
                            table.RowGroups[0].Rows.Add(BuildRow(row, block.ColumnWeights.Length, isHeader: false));
                        }
                    }

                    if (block.IsLast && tableRows > MaxTableRows)
                    {
                        document.Blocks.Add(MutedParagraph($"… +{tableRows - MaxTableRows}"));
                    }

                    break;
                case MarkdownBlockKind.Record:
                    var record = new Paragraph { Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(10, 6, 10, 6) };
                    record.SetResourceReference(Block.BackgroundProperty, "EchoSurfaceAltBrush");
                    string[] cells = block.Rows.Count > 0 ? block.Rows[0] : [];
                    MarkdownInlines.Add(record.Inlines, string.Join("  ·  ", cells
                        .Select((value, c) => (value, name: c < block.Header.Length ? block.Header[c] : string.Empty))
                        .Where(cell => cell.value.Length > 0)
                        .Select(cell => cell.name.Length > 0 ? cell.name + ": " + cell.value : cell.value)), FontSize);
                    document.Blocks.Add(record);
                    break;
                default:
                    var body = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
                    MarkdownInlines.AddLines(body.Inlines, block.Text, FontSize);
                    document.Blocks.Add(body);
                    break;
            }
        }

        if (truncated)
        {
            document.Blocks.Add(MutedParagraph("…"));
        }

        Document = document;
    }

    private const int MaxTableRows = 80;

    private Paragraph MutedParagraph(string text)
    {
        var paragraph = new Paragraph(new Run(text)) { Margin = new Thickness(0, 4, 0, 8) };
        paragraph.SetResourceReference(TextElement.ForegroundProperty, "EchoMutedBrush");
        return paragraph;
    }

    private Table NewTable(MarkdownBlock block)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 2, 0, 12), FontSize = FontSize - 1 };
        foreach (double weight in block.ColumnWeights)
        {
            table.Columns.Add(new TableColumn { Width = new GridLength(weight, GridUnitType.Star) });
        }

        var group = new TableRowGroup();
        group.Rows.Add(BuildRow(block.Header, block.ColumnWeights.Length, isHeader: true));
        table.RowGroups.Add(group);
        return table;
    }

    private TableRow BuildRow(string[] cells, int width, bool isHeader)
    {
        var row = new TableRow();
        if (isHeader)
        {
            row.FontWeight = FontWeights.Bold;
            row.SetResourceReference(System.Windows.Documents.TableRow.BackgroundProperty, "EchoSurfaceAltBrush");
        }

        for (int c = 0; c < width; c++)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0) };
            MarkdownInlines.Add(paragraph.Inlines, c < cells.Length ? cells[c] : string.Empty, FontSize);
            var cell = new TableCell(paragraph) { Padding = new Thickness(8, 4, 8, 4), BorderThickness = new Thickness(0, 0, 0, 1) };
            cell.SetResourceReference(TableCell.BorderBrushProperty, "EchoBorderBrush");
            row.Cells.Add(cell);
        }

        return row;
    }
}
