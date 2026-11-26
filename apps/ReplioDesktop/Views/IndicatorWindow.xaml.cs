using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ReplioDesktop.Services;
using ReplioDesktop.ViewModels;

namespace ReplioDesktop.Views;

public partial class IndicatorWindow : Window
{
    private readonly LocalizationService _localizer;
    private bool _overlayHidden;
    private bool _captureRunning;
    private readonly DispatcherTimer _snapshotResetTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(3.5) };

    private static readonly Brush SavedBackground = Frozen("#14302A");
    private static readonly Brush SavedBorder = Frozen("#245446");
    private static readonly Brush SavedText = Frozen("#5EF0C1");
    private static readonly Brush FailedBackground = Frozen("#3A2230");
    private static readonly Brush FailedBorder = Frozen("#7B4C5F");
    private static readonly Brush FailedText = Frozen("#FFD9E0");

    /// <summary>
    /// Whether the «Что спросить» overlay is on screen now. The button follows the real overlay, not its own last
    /// click: otherwise it got out of step and needed a second press.
    /// </summary>
    public Func<bool>? OverlayShown { get; set; }

    public event Action? HideRequested;
    public event Action? AskRequested;
    public event Action? ToggleCaptureRequested;
    public event Action? SnapshotRequested;
    public event Action? OpenMainRequested;

    public IndicatorWindow(LocalizationService localizer)
    {
        _localizer = localizer;
        InitializeComponent();
        // Hints over another app's window: the built-in tooltips only work in the active window.
        ReplioDesktop.Views.Controls.FloatingHints.Attach(this);
        _localizer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Item[]")
            {
                RefreshOverlayToggleLabel();
            }
        };
        _snapshotResetTimer.Tick += (_, _) => ResetSnapshotButton();
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            RefreshOverlayToggleLabel();
        };
        SetOverlayHidden(true);
        SetCaptureState(false);
    }

    /// <summary>A thin line along the bottom while the plan's remainder is 20 % or less: how much is left, in its colour.</summary>
    public void SetLimit(bool show, int percent, string color, string tooltip)
    {
        LimitLine.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            return;
        }

        int left = Math.Clamp(percent, 0, 100);
        LimitLine.ColumnDefinitions[0].Width = new GridLength(left, GridUnitType.Star);
        LimitLine.ColumnDefinitions[1].Width = new GridLength(100 - left, GridUnitType.Star);
        LimitFill.Background = Frozen(color);
        LimitLine.ToolTip = tooltip;
    }

    /// <summary>The 📷 button answers at once: ⋯ while the screen is taken, ✓ and the number when saved, ✕ when not.</summary>
    public void SetSnapshotStage(SnapshotStage stage, int number)
    {
        _snapshotResetTimer.Stop();
        switch (stage)
        {
            case SnapshotStage.Busy:
                CaptureButton.Content = "⋯";
                CaptureButton.Opacity = 0.7;
                return;
            case SnapshotStage.Saved:
                Paint(number > 0 ? "✓ " + number : "✓", SavedBackground, SavedBorder, SavedText);
                break;
            default:
                Paint("✕", FailedBackground, FailedBorder, FailedText);
                break;
        }

        _snapshotResetTimer.Start();
    }

    /// <summary>A short message in place of «Спросить» for a few seconds: the overlay with its notices is hidden.</summary>
    public void ShowNotice(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _noticeTimer.Stop();
        HideAskButton.Content = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis };
        HideAskButton.ToolTip = text;
        _noticeTimer.Start();
    }

    public void SetOverlayHidden(bool hidden)
    {
        _overlayHidden = hidden;
        RefreshOverlayToggleLabel();
    }

    public void SetCaptureState(bool isRunning)
    {
        _captureRunning = isRunning;
        HomeLogo.IsAnimating = isRunning;
        StopButton.Content = CaptureGlyph(isRunning);
        StopButton.ToolTip = isRunning ? _localizer["IndicatorStopHint"] : _localizer["IndicatorStartHint"];
    }

    private void RefreshOverlayToggleLabel()
    {
        if (_noticeTimer.IsEnabled)
        {
            return;
        }

        HideAskButton.Content = _overlayHidden ? _localizer["IndicatorAsk"] : _localizer["IndicatorHide"];
        HideAskButton.ToolTip = _overlayHidden ? _localizer["TipIndicatorAsk"] : _localizer["TipIndicatorHide"];
        HomeButton.ToolTip = _localizer["IndicatorHomeHint"];
        CaptureButton.ToolTip = _localizer["IndicatorSnapshotHint"];
        StopButton.ToolTip = _captureRunning ? _localizer["IndicatorStopHint"] : _localizer["IndicatorStartHint"];
    }

    private void HideAskButton_OnClick(object sender, RoutedEventArgs e)
    {
        _noticeTimer.Stop();
        if (OverlayShown != null)
        {
            _overlayHidden = !OverlayShown();
        }

        if (_overlayHidden)
        {
            AskRequested?.Invoke();
            SetOverlayHidden(false);
            return;
        }

        HideRequested?.Invoke();
        SetOverlayHidden(true);
    }

    private void StopButton_OnClick(object sender, RoutedEventArgs e)
    {
        ToggleCaptureRequested?.Invoke();
        SetCaptureState(!_captureRunning);
    }

    private void CaptureButton_OnClick(object sender, RoutedEventArgs e)
    {
        SnapshotRequested?.Invoke();
    }

    private void Paint(string content, Brush background, Brush border, Brush text)
    {
        CaptureButton.Opacity = 1;
        CaptureButton.Content = content;
        CaptureButton.Background = background;
        CaptureButton.BorderBrush = border;
        CaptureButton.Foreground = text;
    }

    private void ResetSnapshotButton()
    {
        _snapshotResetTimer.Stop();
        CaptureButton.Opacity = 1;
        CaptureButton.Content = "📷";
        CaptureButton.ClearValue(BackgroundProperty);
        CaptureButton.ClearValue(BorderBrushProperty);
        CaptureButton.ClearValue(ForegroundProperty);
    }

    private static Brush Frozen(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private void HomeButton_OnClick(object sender, RoutedEventArgs e)
    {
        OpenMainRequested?.Invoke();
    }

    private void Window_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private static readonly Brush GlyphBrush = MakeGlyphBrush();

    private static Brush MakeGlyphBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xE9, 0xEE, 0xF5));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Stop (a rounded square) and start (a triangle) are drawn, not typed: a ■ character comes out a different
    /// size in every font, and on macOS it was tiny. The same shapes are used on both systems.
    /// </summary>
    private static UIElement CaptureGlyph(bool isRunning) => isRunning
        ? new System.Windows.Shapes.Rectangle { Width = 11, Height = 11, RadiusX = 2.5, RadiusY = 2.5, Fill = GlyphBrush }
        : new System.Windows.Shapes.Path { Data = Geometry.Parse("M0,0 L10,6 L0,12 Z"), Fill = GlyphBrush, Margin = new Thickness(2, 0, 0, 0) };
}
