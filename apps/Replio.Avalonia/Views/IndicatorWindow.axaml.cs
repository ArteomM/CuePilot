using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using ReplioDesktop.Services;
using ReplioDesktop.ViewModels;

namespace Replio.Avalonia.Views;

public partial class IndicatorWindow : Window
{
    private readonly LocalizationService _localizer = null!;
    private bool _overlayHidden;
    private bool _captureRunning;
    private readonly DispatcherTimer _snapshotResetTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(3.5) };

    private static readonly IBrush SavedBackground = Brush.Parse("#14302A");
    private static readonly IBrush SavedBorder = Brush.Parse("#245446");
    private static readonly IBrush SavedText = Brush.Parse("#5EF0C1");
    private static readonly IBrush FailedBackground = Brush.Parse("#3A2230");
    private static readonly IBrush FailedBorder = Brush.Parse("#7B4C5F");
    private static readonly IBrush FailedText = Brush.Parse("#FFD9E0");

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

    public IndicatorWindow()
    {
        InitializeComponent();
    }

    /// <summary>A thin line along the bottom while the plan's remainder is 20 % or less: how much is left, in its colour.</summary>
    public void SetLimit(bool show, int percent, string color, string tooltip)
    {
        LimitLine.IsVisible = show;
        if (!show)
        {
            return;
        }

        int left = Math.Clamp(percent, 0, 100);
        LimitLine.ColumnDefinitions[0].Width = new GridLength(left, GridUnitType.Star);
        LimitLine.ColumnDefinitions[1].Width = new GridLength(100 - left, GridUnitType.Star);
        LimitFill.Background = Brush.Parse(color);
        ToolTip.SetTip(LimitLine, tooltip);
    }

    public IndicatorWindow(LocalizationService localizer)
        : this()
    {
        _localizer = localizer;
        _localizer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Item[]")
            {
                RefreshOverlayToggleLabel();
            }
        };
        HideAskButton.Click += (_, _) => HideAskButton_OnClick();
        StopButton.Click += (_, _) =>
        {
            ToggleCaptureRequested?.Invoke();
            SetCaptureState(!_captureRunning);
        };
        CaptureButton.Click += (_, _) => SnapshotRequested?.Invoke();
        HomeButton.Click += (_, _) => OpenMainRequested?.Invoke();
        PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
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
        ToolTip.SetTip(HideAskButton, text);
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
        ToolTip.SetTip(StopButton, isRunning ? _localizer["IndicatorStopHint"] : _localizer["IndicatorStartHint"]);
    }

    private void RefreshOverlayToggleLabel()
    {
        if (_noticeTimer.IsEnabled)
        {
            return;
        }

        HideAskButton.Content = _overlayHidden ? _localizer["IndicatorAsk"] : _localizer["IndicatorHide"];
        ToolTip.SetTip(HideAskButton, _overlayHidden ? _localizer["TipIndicatorAsk"] : _localizer["TipIndicatorHide"]);
        ToolTip.SetTip(HomeButton, _localizer["IndicatorHomeHint"]);
        ToolTip.SetTip(CaptureButton, _localizer["IndicatorSnapshotHint"]);
        ToolTip.SetTip(StopButton, _captureRunning ? _localizer["IndicatorStopHint"] : _localizer["IndicatorStartHint"]);
    }

    private void HideAskButton_OnClick()
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

    private void Paint(string content, IBrush background, IBrush border, IBrush text)
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
}
