using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Replio.Avalonia.Views.Controls;

/// <summary>Shared motion settings for the «Эхо» theme (port of ReplioDesktop/Views/Controls/EchoMotion.cs).</summary>
internal static class EchoMotion
{
    public static readonly Color Accent = Color.FromRgb(0x5E, 0xF0, 0xC1);
    public static readonly Color Muted = Color.FromRgb(0x5C, 0x68, 0x78);

    public static readonly IImmutableSolidColorBrush AccentBrush = new ImmutableSolidColorBrush(Accent);

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
/// Base for the small animated marks: draws itself every frame while <see cref="IsRunning"/> is true.
/// Time is the number of seconds since the animation started.
/// </summary>
public abstract class EchoAnimatedControl : Control
{
    private DateTime _startedAt = DateTime.UtcNow;
    private bool _frameRequested;

    protected double Seconds => (DateTime.UtcNow - _startedAt).TotalSeconds;

    protected abstract bool IsRunning { get; }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Restart();
    }

    protected void Restart()
    {
        _startedAt = DateTime.UtcNow;
        RequestNextFrame();
        InvalidateVisual();
    }

    protected void RequestNextFrame()
    {
        if (_frameRequested || !IsRunning || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        _frameRequested = true;
        top.RequestAnimationFrame(_ =>
        {
            _frameRequested = false;
            InvalidateVisual();
            RequestNextFrame();
        });
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            RequestNextFrame();
        }
    }

    protected static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);
}

/// <summary>Logo: the mint sound-wave tile of the site, with echo rings that spread out while Replio is listening.</summary>
public class EchoLogo : EchoAnimatedControl
{
    public static readonly StyledProperty<bool> IsAnimatingProperty =
        AvaloniaProperty.Register<EchoLogo, bool>(nameof(IsAnimating));

    /// <summary>
    /// Live sound level (loudest sample, 0–1) while recording: the five bars move like an equalizer, so it is clear
    /// at a glance that Replio hears something; flat bars mean silence. Below zero: the still mark.
    /// </summary>
    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<EchoLogo, double>(nameof(Level), -1);

    private double _shownLevel;

    private static readonly (double X, double Y, double H)[] Bars = [(7, 13, 6), (11.4, 9, 14), (15.8, 6, 20), (20.2, 10.5, 11), (24.6, 14, 4)];
    private static readonly IBrush Ink = new SolidColorBrush(Color.FromRgb(0x06, 0x2A, 0x1F));

    static EchoLogo()
    {
        AffectsRender<EchoLogo>(IsAnimatingProperty, LevelProperty);
        IsAnimatingProperty.Changed.AddClassHandler<EchoLogo>((logo, _) => logo.Restart());
        LevelProperty.Changed.AddClassHandler<EchoLogo>((logo, _) => logo.RequestNextFrame());
    }

    public double Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public bool IsAnimating
    {
        get => GetValue(IsAnimatingProperty);
        set => SetValue(IsAnimatingProperty, value);
    }

    protected override bool IsRunning => IsAnimating && IsVisible;

    public EchoLogo()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        // The drawing is 120×120 like the WPF Viewbox canvas, scaled uniformly into the control.
        double scale = Math.Min(Bounds.Width, Bounds.Height) / 120.0;
        if (scale <= 0)
        {
            return;
        }

        double offsetX = (Bounds.Width - 120 * scale) / 2;
        double offsetY = (Bounds.Height - 120 * scale) / 2;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY)))
        {
            if (IsRunning)
            {
                // Three rings 0.8 s apart: grow 0.6 → 1.5 (ease out) and fade 0.9 → 0 over 2.4 s.
                for (int i = 0; i < 3; i++)
                {
                    double t = Seconds - 0.8 * i;
                    if (t < 0)
                    {
                        continue;
                    }

                    double phase = t % 2.4 / 2.4;
                    double ringScale = 0.6 + 0.9 * EaseOutCubic(phase);
                    double opacity = 0.9 * (1 - phase);
                    var pen = new Pen(new SolidColorBrush(EchoMotion.Accent, opacity), 2 / ringScale);
                    using (context.PushTransform(Matrix.CreateTranslation(-60, -60) * Matrix.CreateScale(ringScale, ringScale) * Matrix.CreateTranslation(60, 60)))
                    {
                        context.DrawEllipse(null, pen, new Point(60, 60), 36, 36);
                    }
                }
            }

            // The site's mark: a mint tile with a sound wave (favicon.svg, 32×32) scaled into the 72×72 middle.
            const double k = 72 / 32.0;
            context.DrawRectangle(EchoMotion.AccentBrush, null, new RoundedRect(new Rect(24, 24, 72, 72), 9 * k));
            bool live = Level >= 0 && IsRunning;
            if (live)
            {
                // Quick to rise, slower to fall: a word stays visible for a moment.
                double target = EchoMotion.Loudness(Level);
                _shownLevel += (target - _shownLevel) * (target > _shownLevel ? 0.5 : 0.12);
            }
            else
            {
                _shownLevel = 0;
            }

            for (int i = 0; i < Bars.Length; i++)
            {
                (double x, double y, double h) = Bars[i];
                if (live)
                {
                    h = EchoMotion.BarHeight(i, _shownLevel, Seconds);
                    y = 16 - h / 2;
                }

                context.DrawRectangle(Ink, null, new RoundedRect(new Rect(24 + x * k, 24 + y * k, 2.6 * k, h * k), 1.3 * k));
            }
        }
    }
}

/// <summary>Four level bars: they bounce while recording and lie flat and grey when idle.</summary>
public class EchoListeningBars : EchoAnimatedControl
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<EchoListeningBars, bool>(nameof(IsActive));

    static EchoListeningBars()
    {
        AffectsRender<EchoListeningBars>(IsActiveProperty);
        IsActiveProperty.Changed.AddClassHandler<EchoListeningBars>((bars, _) => bars.Restart());
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    protected override bool IsRunning => IsActive && IsVisible;

    protected override Size MeasureOverride(Size availableSize) => new(4 * 3 + 3 * 3, 16);

    public override void Render(DrawingContext context)
    {
        var brush = new SolidColorBrush(IsActive ? EchoMotion.Accent : EchoMotion.Muted);
        double top = (Bounds.Height - 16) / 2;
        for (int i = 0; i < 4; i++)
        {
            double scaleY;
            if (IsRunning)
            {
                // 0.25 ↔ 1 over 0.5 s each way, sine in-out, bars 0.15 s apart.
                double t = Math.Max(0, Seconds - 0.15 * i) % 1.0;
                double half = t < 0.5 ? t / 0.5 : (1 - t) / 0.5;
                scaleY = 0.25 + 0.75 * (0.5 - 0.5 * Math.Cos(Math.PI * half));
            }
            else
            {
                scaleY = IsActive ? 0.6 : 0.3;
            }

            double height = 16 * scaleY;
            double x = i * 6;
            context.DrawRectangle(brush, null, new Rect(x, top + (16 - height) / 2, 3, height), 1.5, 1.5);
        }
    }
}

/// <summary>Three pulsing dots shown while Replio prepares an answer.</summary>
public class EchoThinkingDots : EchoAnimatedControl
{
    protected override bool IsRunning => IsVisible;

    protected override Size MeasureOverride(Size availableSize) => new(3 * 6 + 2 * 6, 6);

    public override void Render(DrawingContext context)
    {
        double top = (Bounds.Height - 6) / 2;
        for (int i = 0; i < 3; i++)
        {
            // 0.2 ↔ 1 over 0.6 s each way, dots 0.2 s apart.
            double t = Math.Max(0, Seconds - 0.2 * i) % 1.2;
            double opacity = 0.2 + 0.8 * (t < 0.6 ? t / 0.6 : (1.2 - t) / 0.6);
            context.DrawEllipse(new SolidColorBrush(EchoMotion.Accent, opacity), null, new Point(3 + i * 12, top + 3), 3, 3);
        }
    }
}
