using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace CuePilotDesktop.Views.Controls;

/// <summary>Shared motion settings for the «Эхо» theme.</summary>
internal static class EchoMotion
{
    public static readonly Color Accent = Color.FromRgb(0x5E, 0xF0, 0xC1);
    public static readonly Color Muted = Color.FromRgb(0x5C, 0x68, 0x78);

    /// <summary>Windows "show animations" setting; when off, every control stays still.</summary>
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    public static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Logo: a «C» with a mint dot and echo rings that spread out while CuePilot is listening.
/// </summary>
public class EchoLogo : UserControl
{
    public static readonly DependencyProperty IsAnimatingProperty = DependencyProperty.Register(
        nameof(IsAnimating),
        typeof(bool),
        typeof(EchoLogo),
        new PropertyMetadata(false, (d, _) => ((EchoLogo)d).UpdateAnimation()));

    private readonly Ellipse[] _rings = new Ellipse[3];
    private readonly Storyboard _storyboard = new();
    private bool _running;

    public EchoLogo()
    {
        var canvas = new Canvas { Width = 120, Height = 120 };
        for (int i = 0; i < _rings.Length; i++)
        {
            var scale = new ScaleTransform(1, 1, 36, 36);
            var ring = new Ellipse
            {
                Width = 72,
                Height = 72,
                Stroke = EchoMotion.Brush(EchoMotion.Accent),
                StrokeThickness = 2,
                Opacity = 0,
                RenderTransform = scale
            };
            Canvas.SetLeft(ring, 24);
            Canvas.SetTop(ring, 24);
            canvas.Children.Add(ring);
            _rings[i] = ring;
            AddRingAnimation(ring, TimeSpan.FromSeconds(0.8 * i));
        }

        var letter = new Path
        {
            Data = Geometry.Parse("M78,42 A26,26 0 1 0 78,78"),
            StrokeThickness = 9,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        // The «C» follows the day/night palette.
        letter.SetResourceReference(Shape.StrokeProperty, "EchoTextBrush");
        canvas.Children.Add(letter);
        var dot = new Ellipse { Width = 12, Height = 12, Fill = EchoMotion.Brush(EchoMotion.Accent) };
        Canvas.SetLeft(dot, 74);
        Canvas.SetTop(dot, 54);
        canvas.Children.Add(dot);

        Content = new Viewbox { Child = canvas, Stretch = Stretch.Uniform };
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => Stop();
    }

    public bool IsAnimating
    {
        get => (bool)GetValue(IsAnimatingProperty);
        set => SetValue(IsAnimatingProperty, value);
    }

    private void AddRingAnimation(Ellipse ring, TimeSpan delay)
    {
        var duration = new Duration(TimeSpan.FromSeconds(2.4));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        foreach (string axis in new[] { "ScaleX", "ScaleY" })
        {
            var grow = new DoubleAnimation(0.6, 1.5, duration) { BeginTime = delay, EasingFunction = ease, RepeatBehavior = RepeatBehavior.Forever };
            Storyboard.SetTarget(grow, ring);
            Storyboard.SetTargetProperty(grow, new PropertyPath($"RenderTransform.{axis}"));
            _storyboard.Children.Add(grow);
        }

        var fade = new DoubleAnimation(0.9, 0, duration) { BeginTime = delay, RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(fade, ring);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        _storyboard.Children.Add(fade);
    }

    private void UpdateAnimation()
    {
        if (IsAnimating && IsLoaded && EchoMotion.Enabled)
        {
            if (!_running)
            {
                _storyboard.Begin(this, true);
                _running = true;
            }
        }
        else
        {
            Stop();
        }
    }

    private void Stop()
    {
        if (!_running)
        {
            return;
        }

        _storyboard.Stop(this);
        _running = false;
        foreach (Ellipse ring in _rings)
        {
            ring.Opacity = 0;
        }
    }
}

/// <summary>Four level bars: they bounce while recording and lie flat and grey when idle.</summary>
public class EchoListeningBars : UserControl
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive),
        typeof(bool),
        typeof(EchoListeningBars),
        new PropertyMetadata(false, (d, _) => ((EchoListeningBars)d).Update()));

    private readonly Rectangle[] _bars = new Rectangle[4];
    private readonly Storyboard _storyboard = new();
    private bool _running;

    public EchoListeningBars()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < _bars.Length; i++)
        {
            var bar = new Rectangle
            {
                Width = 3,
                Height = 16,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Margin = new Thickness(i == 0 ? 0 : 3, 0, 0, 0),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 0.3)
            };
            panel.Children.Add(bar);
            _bars[i] = bar;

            var bounce = new DoubleAnimation(0.25, 1, new Duration(TimeSpan.FromSeconds(0.5)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromSeconds(0.15 * i),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(bounce, bar);
            Storyboard.SetTargetProperty(bounce, new PropertyPath("RenderTransform.ScaleY"));
            _storyboard.Children.Add(bounce);
        }

        Content = panel;
        Loaded += (_, _) => Update();
        Unloaded += (_, _) => Stop();
        Update();
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private void Update()
    {
        Brush fill = EchoMotion.Brush(IsActive ? EchoMotion.Accent : EchoMotion.Muted);
        foreach (Rectangle bar in _bars)
        {
            bar.Fill = fill;
        }

        if (IsActive && IsLoaded && EchoMotion.Enabled)
        {
            if (!_running)
            {
                _storyboard.Begin(this, true);
                _running = true;
            }
        }
        else
        {
            Stop();
        }
    }

    private void Stop()
    {
        if (_running)
        {
            _storyboard.Stop(this);
            _running = false;
        }

        foreach (Rectangle bar in _bars)
        {
            ((ScaleTransform)bar.RenderTransform).ScaleY = IsActive ? 0.6 : 0.3;
        }
    }
}

/// <summary>Three pulsing dots shown while CuePilot prepares an answer.</summary>
public class EchoThinkingDots : UserControl
{
    private readonly Storyboard _storyboard = new();
    private bool _running;

    public EchoThinkingDots()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < 3; i++)
        {
            var dot = new Ellipse
            {
                Width = 6,
                Height = 6,
                Margin = new Thickness(i == 0 ? 0 : 6, 0, 0, 0),
                Fill = EchoMotion.Brush(EchoMotion.Accent),
                Opacity = 0.6
            };
            panel.Children.Add(dot);

            var pulse = new DoubleAnimation(0.2, 1, new Duration(TimeSpan.FromSeconds(0.6)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromSeconds(0.2 * i)
            };
            Storyboard.SetTarget(pulse, dot);
            Storyboard.SetTargetProperty(pulse, new PropertyPath(OpacityProperty));
            _storyboard.Children.Add(pulse);
        }

        Content = panel;
        IsVisibleChanged += (_, _) => Update();
        Loaded += (_, _) => Update();
        Unloaded += (_, _) => Update();
    }

    private void Update()
    {
        bool shouldRun = IsVisible && IsLoaded && EchoMotion.Enabled;
        if (shouldRun && !_running)
        {
            _storyboard.Begin(this, true);
            _running = true;
        }
        else if (!shouldRun && _running)
        {
            _storyboard.Stop(this);
            _running = false;
        }
    }
}
