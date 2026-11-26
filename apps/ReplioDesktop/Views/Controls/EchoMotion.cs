using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace ReplioDesktop.Views.Controls;

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

    /// <summary>A peak (0–1) as it is heard: quiet speech already lifts the bars, loud speech fills them.</summary>
    public static double Loudness(double peak) => peak <= 0 ? 0 : Math.Sqrt(Math.Min(1, peak * 4));

    private static readonly double[] BarWeight = [0.55, 0.85, 1.0, 0.8, 0.5];
    private static readonly double[] BarSpeed = [7.1, 9.3, 6.2, 8.4, 10.7];

    /// <summary>Height of bar <paramref name="index"/> of the mark (in its 32-unit tile, centred at 16): 3 in silence, up to 22.</summary>
    public static double BarHeight(int index, double loudness, double seconds)
    {
        double wobble = 0.72 + 0.28 * Math.Sin(seconds * BarSpeed[index] + index * 1.7);
        return 3 + 19 * Math.Clamp(loudness * BarWeight[index] * wobble * 1.2, 0, 1);
    }
}

/// <summary>
/// Logo: the mint sound-wave tile of the site and echo rings that spread out while Replio is listening.
/// </summary>
public class EchoLogo : UserControl
{
    public static readonly DependencyProperty IsAnimatingProperty = DependencyProperty.Register(
        nameof(IsAnimating),
        typeof(bool),
        typeof(EchoLogo),
        new PropertyMetadata(false, (d, _) => ((EchoLogo)d).UpdateAnimation()));

    /// <summary>
    /// Live sound level (loudest sample, 0–1) while recording: the five bars of the mark move like an equalizer,
    /// so it is clear at a glance that Replio hears something; flat bars mean silence. Below zero: the still mark.
    /// </summary>
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level),
        typeof(double),
        typeof(EchoLogo),
        new PropertyMetadata(-1d, (d, _) => ((EchoLogo)d).OnLevelChanged()));

    private static readonly (double X, double Y, double H)[] Marks = [(7, 13, 6), (11.4, 9, 14), (15.8, 6, 20), (20.2, 10.5, 11), (24.6, 14, 4)];
    private readonly Ellipse[] _rings = new Ellipse[3];
    private readonly Rectangle[] _bars = new Rectangle[5];
    private readonly Storyboard _storyboard = new();
    private readonly System.Windows.Threading.DispatcherTimer _levelTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DateTime _levelStartedAt = DateTime.UtcNow;
    private double _shownLevel;
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

        // The site's mark: a mint tile with a sound wave (favicon.svg, 32×32) scaled into the 72×72 middle.
        const double k = 72 / 32.0;
        var tile = new Rectangle { Width = 72, Height = 72, RadiusX = 9 * k, RadiusY = 9 * k, Fill = EchoMotion.Brush(EchoMotion.Accent) };
        Canvas.SetLeft(tile, 24);
        Canvas.SetTop(tile, 24);
        canvas.Children.Add(tile);
        var ink = EchoMotion.Brush(Color.FromRgb(0x06, 0x2A, 0x1F));
        for (int i = 0; i < Marks.Length; i++)
        {
            (double x, double y, double h) = Marks[i];
            var bar = new Rectangle { Width = 2.6 * k, Height = h * k, RadiusX = 1.3 * k, RadiusY = 1.3 * k, Fill = ink };
            Canvas.SetLeft(bar, 24 + x * k);
            Canvas.SetTop(bar, 24 + y * k);
            canvas.Children.Add(bar);
            _bars[i] = bar;
        }

        _levelTimer.Tick += (_, _) => DrawLevel();
        Content = new Viewbox { Child = canvas, Stretch = Stretch.Uniform };
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => { Stop(); _levelTimer.Stop(); };
    }

    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    private void OnLevelChanged()
    {
        if (Level >= 0 && EchoMotion.Enabled)
        {
            if (!_levelTimer.IsEnabled)
            {
                _levelTimer.Start();
            }

            return;
        }

        // Back to the still mark.
        _levelTimer.Stop();
        _shownLevel = 0;
        const double k = 72 / 32.0;
        for (int i = 0; i < _bars.Length; i++)
        {
            _bars[i].Height = Marks[i].H * k;
            Canvas.SetTop(_bars[i], 24 + Marks[i].Y * k);
        }
    }

    private void DrawLevel()
    {
        const double k = 72 / 32.0;
        double target = EchoMotion.Loudness(Level);
        // Quick to rise, slower to fall: a word stays visible for a moment.
        _shownLevel += (target - _shownLevel) * (target > _shownLevel ? 0.6 : 0.2);
        double seconds = (DateTime.UtcNow - _levelStartedAt).TotalSeconds;
        for (int i = 0; i < _bars.Length; i++)
        {
            double h = EchoMotion.BarHeight(i, _shownLevel, seconds);
            _bars[i].Height = h * k;
            Canvas.SetTop(_bars[i], 24 + (16 - h / 2) * k);
        }
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

/// <summary>Three pulsing dots shown while Replio prepares an answer.</summary>
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
