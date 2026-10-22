using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CuePilot.Avalonia.Platform;
using CuePilot.Avalonia.Views.Dialogs;
using CuePilotDesktop.Services;
using CuePilotDesktop.ViewModels;

namespace CuePilot.Avalonia.Views;

/// <summary>macOS system integration: global hotkeys, hiding the windows from screen sharing, permissions, the Dock.</summary>
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

        // A click on the Dock icon while the window is hidden brings it back.
        if (global::Avalonia.Application.Current?.TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
        {
            activatable.Activated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Reopen)
                {
                    OnUi(RestoreFromTray);
                }
            };
        }

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

    /// <summary>
    /// Before each recording: asks for the microphone the first time, then explains what is missing.
    /// Screen &amp; System Audio Recording is needed for the other side of the call; macOS applies it after a restart.
    /// </summary>
    private bool CheckMacPermissions()
    {
        if (MacPermissions.Microphone == MacPermissions.MicrophoneState.NotDetermined)
        {
            WaitForExit(MacPermissions.StartMicrophoneRequest(Path.Combine(AppContext.BaseDirectory, "cuepilot-audio")));
        }

        if (MacPermissions.Microphone is MacPermissions.MicrophoneState.Denied or MacPermissions.MicrophoneState.Restricted)
        {
            _viewModel.AppendLog("[permissions] Microphone access is off.");
            if (EchoDialog.Ask(this, _viewModel.L, _viewModel.L["MacPermissionsTitle"], _viewModel.L["MacPermissionMicDenied"], _viewModel.L["MacPermissionOpenSettings"]))
            {
                MacPermissions.OpenMicrophoneSettings();
            }

            return false;
        }

        if (!MacPermissions.ScreenCaptureGranted)
        {
            _viewModel.AppendLog("[permissions] Screen & System Audio Recording is off.");
            // The first request shows the system prompt and adds CuePilot to the list in System Settings.
            MacPermissions.RequestScreenCapture();
            if (EchoDialog.Ask(this, _viewModel.L, _viewModel.L["MacPermissionsTitle"], _viewModel.L["MacPermissionScreenDenied"], _viewModel.L["MacPermissionOpenSettings"]))
            {
                MacPermissions.OpenScreenCaptureSettings();
            }

            return false;
        }

        return true;
    }

    /// <summary>Waits for the macOS prompt without freezing the window: the UI keeps running in a nested frame.</summary>
    private static void WaitForExit(Process? process)
    {
        if (process == null)
        {
            return;
        }

        using (process)
        {
            var frame = new DispatcherFrame();
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Dispatcher.UIThread.Post(() => frame.Continue = false);
            if (process.HasExited)
            {
                return;
            }

            // After two minutes without an answer the recording start goes on and the state is read as it is.
            var timeout = DispatcherTimer.RunOnce(() => frame.Continue = false, TimeSpan.FromMinutes(2));
            Dispatcher.UIThread.PushFrame(frame);
            timeout.Dispose();
        }
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
