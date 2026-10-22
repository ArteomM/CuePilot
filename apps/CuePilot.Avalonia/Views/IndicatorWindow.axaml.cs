using Avalonia.Controls;
using CuePilotDesktop.Services;

namespace CuePilot.Avalonia.Views;

public partial class IndicatorWindow : Window
{
    private readonly LocalizationService _localizer = null!;
    private bool _overlayHidden;
    private bool _captureRunning;

    public event Action? HideRequested;
    public event Action? AskRequested;
    public event Action? ToggleCaptureRequested;
    public event Action? SnapshotRequested;
    public event Action? OpenMainRequested;

    public IndicatorWindow()
    {
        InitializeComponent();
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
        ToolTip.SetTip(StopButton, isRunning ? _localizer["IndicatorStopHint"] : _localizer["IndicatorStartHint"]);
    }

    private void RefreshOverlayToggleLabel()
    {
        HideAskButton.Content = _overlayHidden ? _localizer["IndicatorAsk"] : _localizer["IndicatorHide"];
        ToolTip.SetTip(HideAskButton, _overlayHidden ? _localizer["TipIndicatorAsk"] : _localizer["TipIndicatorHide"]);
        ToolTip.SetTip(HomeButton, _localizer["IndicatorHomeHint"]);
        ToolTip.SetTip(CaptureButton, _localizer["IndicatorSnapshotHint"]);
        ToolTip.SetTip(StopButton, _captureRunning ? _localizer["IndicatorStopHint"] : _localizer["IndicatorStartHint"]);
    }

    private void HideAskButton_OnClick()
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
}
