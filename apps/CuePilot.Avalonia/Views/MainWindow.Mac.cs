using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CuePilot.Avalonia.Platform;
using CuePilotDesktop.Services;
using CuePilotDesktop.ViewModels;

namespace CuePilot.Avalonia.Views;

/// <summary>macOS system integration: global hotkeys and hiding the windows from screen sharing.</summary>
public partial class MainWindow
{
    private MacHotkeyManager? _hotkeyManager;
    private string _reportedHotkeyFailures = string.Empty;

    private void InitializeMacIntegration(AppSettings settings)
    {
        MacScreenShareProtection.Initialize(settings.ShowInScreenShare);
        _viewModel.HotkeysChanged += () => OnUi(RegisterHotkeys);
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ShowInScreenShare))
            {
                OnUi(() => MacScreenShareProtection.SetShowInScreenShare(_viewModel.ShowInScreenShare, AllWindows()));
            }
        };

        Opened += (_, _) =>
        {
            if (!OperatingSystem.IsMacOS() || _hotkeyManager != null)
            {
                return;
            }

            try
            {
                _hotkeyManager = new MacHotkeyManager(id => _viewModel.HandleHotkey(id));
            }
            catch (Exception ex)
            {
                _viewModel.AppendLog("[hotkey] Hotkeys are unavailable: " + ex.Message);
                return;
            }

            RegisterHotkeys();
        };
    }

    private IEnumerable<Window> AllWindows() =>
        global::Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows
            : [this, _assistOverlayWindow, _indicatorWindow];

    private void RegisterHotkeys()
    {
        if (_hotkeyManager == null)
        {
            return;
        }

        _hotkeyManager.UnregisterAll();
        if (!_viewModel.TryBuildHotkeyMap(out Dictionary<int, MacHotkeyGesture> hotkeys, out List<string> errors))
        {
            ShowNotification(_viewModel.L["ErrorTitle"], string.Join(Environment.NewLine, errors), true);
            return;
        }

        var failedHotkeys = new List<string>();
        foreach (var pair in hotkeys)
        {
            if (_hotkeyManager.Register(pair.Key, pair.Value, out string error))
            {
                continue;
            }

            _viewModel.AppendLog($"[hotkey] Not registered {pair.Value.DisplayText}: {error}");
            failedHotkeys.Add(pair.Value.DisplayText);
        }

        // One message for all of them, and only when the set changes: saving other settings must not repeat it.
        string failedKey = string.Join(", ", failedHotkeys);
        if (failedHotkeys.Count > 0 && failedKey != _reportedHotkeyFailures)
        {
            ShowNotification(
                _viewModel.L["ErrorTitle"],
                string.Format(_viewModel.L["HotkeysRegisterFailedMessage"], string.Join(Environment.NewLine, failedHotkeys.Select(h => "• " + h))),
                true);
        }

        _reportedHotkeyFailures = failedKey;
    }
}
