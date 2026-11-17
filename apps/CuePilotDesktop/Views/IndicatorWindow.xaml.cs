using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CuePilotDesktop.Services;
using CuePilotDesktop.ViewModels;

namespace CuePilotDesktop.Views;

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
        CuePilotDesktop.Views.Controls.FloatingHints.Attach(this);
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
        StopButton.Content = isRunning ? "■" : "▶";
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
}
