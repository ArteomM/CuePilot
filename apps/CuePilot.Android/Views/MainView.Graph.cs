using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CuePilot.Avalonia.Views.Controls;
using CuePilotDesktop.Services;

namespace CuePilot.Android.Views;

/// <summary>
/// The «Граф» tab on the phone: type names in the legend, zoom buttons (a touch screen has no wheel), and a tap on a
/// node opens a sheet with what the desktop shows in its side panel: type, description, links and sources.
/// </summary>
public partial class MainView
{
    private static readonly string[] GraphTypes = ["person", "organization", "project", "task", "decision", "document", "date", "place", "term"];

    private void SetUpGraph()
    {
        TopicGraphView.NodeSelected += node => OnUi(() => ShowGraphNode(node));
        TopicGraphView.LayoutChanged += _viewModel.SaveGraphLayout;
        UpdateGraphTypeNames();
    }

    private void UpdateGraphTypeNames()
    {
        TopicGraphView.TypeNames = GraphTypes.ToDictionary(type => type, type => _viewModel.L["GraphType_" + type]);
        TopicGraphView.InvalidateVisual();
    }

    private void ShowGraphNode(GraphNodeData? node)
    {
        if (node == null)
        {
            return;
        }

        string type = node.Type.ToLowerInvariant();
        var fields = new List<Control>();
        var typeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        typeRow.Children.Add(new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(5),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(GraphView.ColorFor(type))
        });
        typeRow.Children.Add(new TextBlock
        {
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 14,
            Text = TopicGraphView.TypeNames.TryGetValue(type, out string? name) ? name : type,
            Foreground = Brush("EchoMutedBrush")
        });
        fields.Add(typeRow);

        if (!string.IsNullOrWhiteSpace(node.Description))
        {
            fields.Add(SheetText(node.Description, 15, "EchoSoftTextBrush", new Thickness(0, 10, 0, 0)));
        }

        var links = TopicGraphView.GetLinks(node);
        if (links.Count > 0)
        {
            fields.Add(SheetText(_viewModel.L["GraphDetailsLinks"], 15, "EchoTextBrush", new Thickness(0, 14, 0, 2), bold: true));
            var list = new StackPanel();
            foreach ((string label, string relation, GraphNodeData target) in links.Take(12))
            {
                var button = new Button
                {
                    Classes = { "ghost" },
                    Margin = new Thickness(0, 4, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Content = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Left,
                        Text = string.IsNullOrWhiteSpace(relation) ? label : relation + " → " + label
                    }
                };
                button.Click += (_, _) =>
                {
                    TopicGraphView.SelectNode(target);
                    ShowGraphNode(target);
                };
                list.Children.Add(button);
            }

            fields.Add(new ScrollViewer { MaxHeight = 260, Content = list });
        }

        if (node.Sources.Count > 0)
        {
            fields.Add(SheetText(_viewModel.L["GraphDetailsSources"] + ": " + string.Join(", ", node.Sources), 13.5, "EchoMutedBrush", new Thickness(0, 12, 0, 0)));
        }

        ShowDialog(node.Label, string.Empty, [(_viewModel.L["DialogOk"], () => TopicGraphView.SelectNode(null), false)], fields);
    }

    private TextBlock SheetText(string text, double size, string brush, Thickness margin, bool bold = false) => new()
    {
        Text = text,
        Margin = margin,
        FontSize = size,
        LineHeight = size * 1.4,
        FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush(brush)
    };
}
