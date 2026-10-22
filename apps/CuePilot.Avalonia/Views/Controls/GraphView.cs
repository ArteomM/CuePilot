using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using CuePilotDesktop.Services;

namespace CuePilot.Avalonia.Views.Controls;

/// <summary>
/// Force-directed graph of entities in the style of Obsidian: drag nodes, drag the background to pan,
/// wheel to zoom, hover to highlight neighbours, click a node to see its details (port of the WPF GraphView).
/// </summary>
public class GraphView : Control
{
    public static readonly StyledProperty<TopicGraph?> GraphProperty =
        AvaloniaProperty.Register<GraphView, TopicGraph?>(nameof(Graph));

    public static readonly StyledProperty<string?> FilterProperty =
        AvaloniaProperty.Register<GraphView, string?>(nameof(Filter), string.Empty);

    private static readonly Dictionary<string, Color> TypeColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["person"] = Color.FromRgb(0x4F, 0xD1, 0xA5),
        ["organization"] = Color.FromRgb(0x7A, 0xA7, 0xFF),
        ["project"] = Color.FromRgb(0xF2, 0xB5, 0x44),
        ["task"] = Color.FromRgb(0xFF, 0x8A, 0x65),
        ["decision"] = Color.FromRgb(0xC7, 0x92, 0xEA),
        ["document"] = Color.FromRgb(0x64, 0xB5, 0xF6),
        ["date"] = Color.FromRgb(0xF4, 0x8F, 0xB1),
        ["place"] = Color.FromRgb(0x81, 0xC7, 0x84),
        ["term"] = Color.FromRgb(0xA0, 0xA9, 0xB8)
    };

    private readonly List<SimNode> _nodes = [];
    private readonly List<(SimNode A, SimNode B, string Label)> _edges = [];
    private readonly Dictionary<SimNode, HashSet<SimNode>> _neighbours = [];
    private double _zoom = 1;
    private Vector _offset;
    private bool _offsetInitialized;
    private double _alpha;
    private bool _animating;
    private bool _attached;
    private SimNode? _hovered;
    private SimNode? _selected;
    private SimNode? _dragged;
    private Point _mouseDown;
    private Point _lastMouse;
    private bool _panning;
    private bool _moved;
    private bool _captured;

    static GraphView()
    {
        GraphProperty.Changed.AddClassHandler<GraphView>((view, _) => view.LoadGraph());
        FilterProperty.Changed.AddClassHandler<GraphView>((view, _) => view.InvalidateVisual());
        AffectsRender<GraphView>(GraphProperty, FilterProperty);
    }

    public GraphView()
    {
        ClipToBounds = true;
        Focusable = true;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>The user selected a node (or cleared the selection with null).</summary>
    public event Action<GraphNodeData?>? NodeSelected;

    /// <summary>The layout settled or a node was dragged: positions were written back to the graph data.</summary>
    public event Action? LayoutChanged;

    public TopicGraph? Graph
    {
        get => GetValue(GraphProperty);
        set => SetValue(GraphProperty, value);
    }

    public string? Filter
    {
        get => GetValue(FilterProperty);
        set => SetValue(FilterProperty, value);
    }

    /// <summary>Localized names of the node types for the legend.</summary>
    public IReadOnlyDictionary<string, string> TypeNames { get; set; } = new Dictionary<string, string>();

    public static Color ColorFor(string type) => TypeColors.TryGetValue(type ?? string.Empty, out Color color) ? color : TypeColors["term"];

    private double ActualWidth => Bounds.Width;

    private double ActualHeight => Bounds.Height;

    /// <summary>Labels and descriptions of the neighbours of a node, for the details panel.</summary>
    public IReadOnlyList<(string Label, string Relation, GraphNodeData Node)> GetLinks(GraphNodeData data)
    {
        SimNode? node = _nodes.FirstOrDefault(n => ReferenceEquals(n.Data, data));
        if (node == null)
        {
            return [];
        }

        return _edges
            .Where(e => e.A == node || e.B == node)
            .Select(e => (e.A == node ? e.B : e.A, e.Label, Outgoing: e.A == node))
            .Select(x => (x.Item1.Data.Label, x.Outgoing ? x.Label : "← " + x.Label, x.Item1.Data))
            .OrderBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public void SelectNode(GraphNodeData? data)
    {
        _selected = data == null ? null : _nodes.FirstOrDefault(n => ReferenceEquals(n.Data, data));
        if (_selected != null)
        {
            // Bring the node into view.
            Point screen = ToScreen(_selected.X, _selected.Y);
            if (screen.X < 40 || screen.Y < 40 || screen.X > ActualWidth - 40 || screen.Y > ActualHeight - 40)
            {
                _offset = new Vector(ActualWidth / 2 - _selected.X * _zoom, ActualHeight / 2 - _selected.Y * _zoom);
            }
        }

        InvalidateVisual();
    }

    /// <summary>Zooms and pans so the whole graph fits.</summary>
    public void FitToView()
    {
        if (_nodes.Count == 0 || ActualWidth < 10 || ActualHeight < 10)
        {
            return;
        }

        double minX = _nodes.Min(n => n.X), maxX = _nodes.Max(n => n.X);
        double minY = _nodes.Min(n => n.Y), maxY = _nodes.Max(n => n.Y);
        double width = Math.Max(1, maxX - minX), height = Math.Max(1, maxY - minY);
        _zoom = Math.Clamp(Math.Min((ActualWidth - 120) / width, (ActualHeight - 120) / height), 0.2, 2.2);
        _offset = new Vector(ActualWidth / 2 - (minX + width / 2) * _zoom, ActualHeight / 2 - (minY + height / 2) * _zoom);
        _offsetInitialized = true;
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        if (_alpha > 0.005)
        {
            StartAnimation();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        StopAnimation();
        base.OnDetachedFromVisualTree(e);
    }

    private void LoadGraph()
    {
        _nodes.Clear();
        _edges.Clear();
        _neighbours.Clear();
        _hovered = _selected = _dragged = null;
        TopicGraph? graph = Graph;
        if (graph == null)
        {
            StopAnimation();
            InvalidateVisual();
            return;
        }

        var random = new Random(graph.Nodes.Count * 7919 + graph.Edges.Count);
        var byId = new Dictionary<string, SimNode>();
        bool allPlaced = graph.Nodes.Count > 0;
        for (int i = 0; i < graph.Nodes.Count; i++)
        {
            GraphNodeData data = graph.Nodes[i];
            bool placed = data.X.HasValue && data.Y.HasValue;
            allPlaced &= placed;
            double angle = i * 2.399963; // golden angle: an even first spread
            double radius = 30 + 18 * Math.Sqrt(i);
            var node = new SimNode(data)
            {
                X = placed ? data.X!.Value : Math.Cos(angle) * radius + random.NextDouble(),
                Y = placed ? data.Y!.Value : Math.Sin(angle) * radius + random.NextDouble()
            };
            _nodes.Add(node);
            _neighbours[node] = [];
            byId[data.Id] = node;
        }

        foreach (GraphEdgeData edge in graph.Edges)
        {
            if (byId.TryGetValue(edge.Source, out SimNode? a) && byId.TryGetValue(edge.Target, out SimNode? b) && a != b)
            {
                _edges.Add((a, b, edge.Label));
                _neighbours[a].Add(b);
                _neighbours[b].Add(a);
            }
        }

        foreach (SimNode node in _nodes)
        {
            node.Degree = _neighbours[node].Count;
            node.Radius = 5 + Math.Sqrt(node.Degree) * 2.6;
        }

        _offsetInitialized = false;
        _alpha = allPlaced ? 0.05 : 1;
        StartAnimation();
        Dispatcher.UIThread.Post(FitToView, DispatcherPriority.Loaded);
        InvalidateVisual();
    }

    private void StartAnimation()
    {
        if (_animating || !_attached)
        {
            return;
        }

        _animating = true;
        RequestFrame();
    }

    private void RequestFrame() => TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_ => OnRendering());

    private void StopAnimation() => _animating = false;

    private void OnRendering()
    {
        if (!_animating)
        {
            return;
        }

        Step();
        InvalidateVisual();
        if (_alpha < 0.005 && _dragged == null)
        {
            StopAnimation();
            WriteBack();
            return;
        }

        RequestFrame();
    }

    /// <summary>One tick of the simulation: nodes repel, links pull, everything drifts to the centre.</summary>
    private void Step()
    {
        const double repulsion = 2600;
        const double linkLength = 95;
        const double linkStrength = 0.06;
        const double gravity = 0.012;
        int count = _nodes.Count;
        for (int i = 0; i < count; i++)
        {
            SimNode a = _nodes[i];
            for (int j = i + 1; j < count; j++)
            {
                SimNode b = _nodes[j];
                double dx = a.X - b.X, dy = a.Y - b.Y;
                double distance2 = dx * dx + dy * dy;
                if (distance2 < 1)
                {
                    dx = (i - j) * 0.5;
                    dy = 0.5;
                    distance2 = 1;
                }

                double force = repulsion / distance2 * _alpha;
                double distance = Math.Sqrt(distance2);
                double fx = dx / distance * force, fy = dy / distance * force;
                a.Vx += fx;
                a.Vy += fy;
                b.Vx -= fx;
                b.Vy -= fy;
            }
        }

        foreach ((SimNode a, SimNode b, _) in _edges)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double distance = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
            double force = (distance - linkLength) * linkStrength * _alpha;
            double fx = dx / distance * force, fy = dy / distance * force;
            a.Vx += fx;
            a.Vy += fy;
            b.Vx -= fx;
            b.Vy -= fy;
        }

        foreach (SimNode node in _nodes)
        {
            node.Vx -= node.X * gravity * _alpha;
            node.Vy -= node.Y * gravity * _alpha;
            if (node == _dragged)
            {
                node.Vx = node.Vy = 0;
                continue;
            }

            node.Vx *= 0.6;
            node.Vy *= 0.6;
            node.X += Math.Clamp(node.Vx, -40, 40);
            node.Y += Math.Clamp(node.Vy, -40, 40);
        }

        _alpha *= 0.985;
    }

    private void WriteBack()
    {
        foreach (SimNode node in _nodes)
        {
            node.Data.X = Math.Round(node.X, 1);
            node.Data.Y = Math.Round(node.Y, 1);
        }

        LayoutChanged?.Invoke();
    }

    private Point ToScreen(double x, double y) => new(x * _zoom + _offset.X, y * _zoom + _offset.Y);

    private Point ToWorld(Point screen) => new((screen.X - _offset.X) / _zoom, (screen.Y - _offset.Y) / _zoom);

    private SimNode? HitTest(Point screen)
    {
        Point world = ToWorld(screen);
        SimNode? best = null;
        double bestDistance = double.MaxValue;
        foreach (SimNode node in _nodes)
        {
            double dx = node.X - world.X, dy = node.Y - world.Y;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            double reach = node.Radius + 6 / _zoom;
            if (distance <= reach && distance < bestDistance)
            {
                best = node;
                bestDistance = distance;
            }
        }

        return best;
    }

    private IBrush FindBrush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out object? value) && value is IBrush brush ? brush : fallback;

    public override void Render(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        if (_nodes.Count == 0)
        {
            return;
        }

        if (!_offsetInitialized && ActualWidth > 0)
        {
            _offset = new Vector(ActualWidth / 2, ActualHeight / 2);
            _offsetInitialized = true;
        }

        IBrush textBrush = FindBrush("EchoTextBrush", Brushes.White);
        IBrush mutedBrush = FindBrush("EchoMutedBrush", Brushes.Gray);
        IBrush lineBrush = FindBrush("EchoBorderBrush", Brushes.DimGray);
        IBrush accentBrush = FindBrush("EchoAccentBrush", Brushes.MediumAquamarine);
        IBrush surfaceBrush = FindBrush("EchoSurfaceBrush", Brushes.Black);
        FontFamily font = this.TryFindResource("EchoFont", out object? f) && f is FontFamily family ? family : FontFamily.Default;
        var typeface = new Typeface(font, FontStyle.Normal, FontWeight.SemiBold);

        SimNode? focus = _hovered ?? _selected;
        HashSet<SimNode>? focusSet = focus == null ? null : [focus, .. _neighbours[focus]];
        string filter = (Filter ?? string.Empty).Trim();
        bool filtering = filter.Length > 0;
        bool Matches(SimNode n) => n.Data.Label.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
            n.Data.Description.Contains(filter, StringComparison.CurrentCultureIgnoreCase);

        var normalPen = new Pen(lineBrush, Math.Max(0.8, 1.1 * Math.Sqrt(_zoom)));
        var focusPen = new Pen(accentBrush, Math.Max(1.2, 1.8 * Math.Sqrt(_zoom)));
        foreach ((SimNode a, SimNode b, string label) in _edges)
        {
            bool highlighted = focus != null && (a == focus || b == focus);
            bool dimmed = (focus != null && !highlighted) || (filtering && !Matches(a) && !Matches(b));
            using (dc.PushOpacity(dimmed ? 0.18 : highlighted ? 1 : 0.7))
            {
                Point pa = ToScreen(a.X, a.Y), pb = ToScreen(b.X, b.Y);
                dc.DrawLine(highlighted ? focusPen : normalPen, pa, pb);
                if (highlighted && !string.IsNullOrWhiteSpace(label))
                {
                    var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 11, mutedBrush);
                    Point middle = new((pa.X + pb.X) / 2 - text.Width / 2, (pa.Y + pb.Y) / 2 - text.Height / 2);
                    dc.DrawRectangle(surfaceBrush, null, new Rect(middle.X - 4, middle.Y - 1, text.Width + 8, text.Height + 2), 4, 4);
                    dc.DrawText(text, middle);
                }
            }
        }

        foreach (SimNode node in _nodes.OrderBy(n => focusSet?.Contains(n) == true ? 1 : 0))
        {
            bool inFocus = focusSet?.Contains(node) == true;
            bool matches = filtering && Matches(node);
            bool dimmed = (focusSet != null && !inFocus) || (filtering && !matches);
            Point center = ToScreen(node.X, node.Y);
            double radius = node.Radius * Math.Max(0.55, Math.Sqrt(_zoom));
            var fill = new SolidColorBrush(ColorFor(node.Data.Type));
            using (dc.PushOpacity(dimmed ? 0.22 : 1))
            {
                if (node == _selected || matches)
                {
                    dc.DrawEllipse(null, new Pen(accentBrush, 2.5), center, radius + 4, radius + 4);
                }

                dc.DrawEllipse(fill, node == _hovered ? new Pen(textBrush, 1.5) : null, center, radius, radius);
                bool showLabel = inFocus || matches || _zoom >= 0.85 || node.Degree >= 4;
                if (showLabel)
                {
                    double size = Math.Clamp(12 * Math.Sqrt(_zoom), 10, 15);
                    var text = new FormattedText(node.Data.Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, inFocus || matches ? textBrush : mutedBrush)
                    {
                        MaxTextWidth = 180,
                        MaxLineCount = 2,
                        Trimming = TextTrimming.CharacterEllipsis,
                        TextAlignment = TextAlignment.Center
                    };
                    dc.DrawText(text, new Point(center.X - 90, center.Y + radius + 3));
                }
            }
        }

        DrawLegend(dc, typeface, mutedBrush);
    }

    private void DrawLegend(DrawingContext dc, Typeface typeface, IBrush textBrush)
    {
        var types = _nodes.Select(n => n.Data.Type.ToLowerInvariant()).Distinct().OrderBy(t => t).ToList();
        double x = 14, y = ActualHeight - 26;
        foreach (string type in types)
        {
            string name = TypeNames.TryGetValue(type, out string? localized) ? localized : type;
            var text = new FormattedText(name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 11.5, textBrush);
            if (x + text.Width + 26 > ActualWidth)
            {
                break;
            }

            dc.DrawEllipse(new SolidColorBrush(ColorFor(type)), null, new Point(x + 5, y + text.Height / 2), 5, 5);
            dc.DrawText(text, new Point(x + 14, y));
            x += text.Width + 30;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus();
        _mouseDown = _lastMouse = e.GetPosition(this);
        _moved = false;
        if (e.ClickCount == 2 && HitTest(_mouseDown) == null)
        {
            FitToView();
            return;
        }

        _dragged = HitTest(_mouseDown);
        _panning = _dragged == null;
        e.Pointer.Capture(this);
        _captured = true;
        if (_dragged != null)
        {
            _alpha = Math.Max(_alpha, 0.25);
            StartAnimation();
        }

        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point position = e.GetPosition(this);
        if (_captured)
        {
            Vector delta = position - _lastMouse;
            Vector total = position - _mouseDown;
            if (Math.Sqrt(total.X * total.X + total.Y * total.Y) > 3)
            {
                _moved = true;
            }

            if (_dragged != null)
            {
                Point world = ToWorld(position);
                _dragged.X = world.X;
                _dragged.Y = world.Y;
                _alpha = Math.Max(_alpha, 0.12);
                StartAnimation();
            }
            else if (_panning)
            {
                _offset += delta;
                Cursor = new Cursor(StandardCursorType.SizeAll);
            }

            _lastMouse = position;
            InvalidateVisual();
            return;
        }

        SimNode? hovered = HitTest(position);
        if (hovered != _hovered)
        {
            _hovered = hovered;
            Cursor = hovered != null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_captured)
        {
            return;
        }

        _captured = false;
        e.Pointer.Capture(null);
        Cursor = _hovered != null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        if (!_moved)
        {
            _selected = _dragged;
            NodeSelected?.Invoke(_selected?.Data);
        }
        else if (_dragged != null)
        {
            WriteBack();
        }

        _dragged = null;
        _panning = false;
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _captured = false;
        _dragged = null;
        _panning = false;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hovered != null && !_captured)
        {
            _hovered = null;
            InvalidateVisual();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Point position = e.GetPosition(this);
        Point world = ToWorld(position);
        _zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15), 0.15, 4);
        _offset = new Vector(position.X - world.X * _zoom, position.Y - world.Y * _zoom);
        InvalidateVisual();
        e.Handled = true;
    }

    private sealed class SimNode(GraphNodeData data)
    {
        public GraphNodeData Data { get; } = data;
        public double X { get; set; }
        public double Y { get; set; }
        public double Vx { get; set; }
        public double Vy { get; set; }
        public int Degree { get; set; }
        public double Radius { get; set; } = 6;
    }
}
