using System.Windows;
using System.Windows.Input;
using CuePilotDesktop.Services;

namespace CuePilotDesktop.Views;

public partial class IndicatorWindow : Window
{
    private readonly LocalizationService _localizer;
    private bool _overlayHidden;
    private bool _captureRunning;

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
        SetOverlayHidden(true);
        SetCaptureState(false);
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
        HideAskButton.Content = _overlayHidden ? _localizer["IndicatorAsk"] : _localizer["IndicatorHide"];
        HideAskButton.ToolTip = _overlayHidden ? _localizer["TipIndicatorAsk"] : _localizer["TipIndicatorHide"];
        HomeButton.ToolTip = _localizer["IndicatorHomeHint"];
        CaptureButton.ToolTip = _localizer["IndicatorSnapshotHint"];
        StopButton.ToolTip = _captureRunning ? _localizer["IndicatorStopHint"] : _localizer["IndicatorStartHint"];
    }

    private void HideAskButton_OnClick(object sender, RoutedEventArgs e)
    {
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
