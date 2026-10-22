using System.IO;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using Forms = global::System.Windows.Forms;
using CuePilotDesktop.Services;
using CuePilotDesktop.ViewModels;
using CuePilotDesktop.Views;

namespace CuePilotDesktop;

public partial class MainWindow : Window
{
    private const int WmHotKey = 0x0312;

    private readonly string _projectRoot;
    private readonly MainViewModel _viewModel;
    private readonly VisualContextCaptureService _visualContextCapture;
    private readonly MiniOverlayWindow _assistOverlayWindow;
    private readonly IndicatorWindow _indicatorWindow;
    private Forms.NotifyIcon? _notifyIcon;
    private bool _allowRealClose;
    private HwndSource? _source;
    private HotkeyManager? _hotkeyManager;
    private string _reportedHotkeyFailures = string.Empty;
    private DateTime _deactivatedAt = DateTime.MinValue;
    private bool _auxWindowsPositioned;

    public MainWindow()
    {
        string projectRoot = ResolveProjectRoot();
        _projectRoot = projectRoot;
        var settingsService = new JsonSettingsService();
        AppSettings settings = settingsService.Load();

        var localizationService = new LocalizationService();
        localizationService.SetLanguage(settings.UiLanguage);
        ScreenShareProtection.Initialize(settings.ShowInScreenShare);

        var diagnostics = new DiagnosticsViewModel(new EnvironmentDiagnosticsService(projectRoot));
        var captureOrchestrator = new CaptureOrchestratorService();
        _visualContextCapture = new VisualContextCaptureService();

        _assistOverlayWindow = new MiniOverlayWindow(new OverlayPositionStore(), localizationService);
        _indicatorWindow = new IndicatorWindow(localizationService);
        ScreenShareProtection.Track(_assistOverlayWindow);
        ScreenShareProtection.Track(_indicatorWindow);
        _viewModel = new MainViewModel(projectRoot, captureOrchestrator, _visualContextCapture, diagnostics, settingsService, localizationService);
        _assistOverlayWindow.ConfigureActions(
            () =>
            {
                if (FoldIfInFront())
                {
                    return;
                }

                if (_viewModel.OpenActiveSessionCommand.CanExecute(null))
                {
                    _viewModel.OpenActiveSessionCommand.Execute(null);
                }

                RestoreFromTray();
            },
            () =>
            {
                if (_viewModel.SuggestNextQuestionCommand.CanExecute(null))
                {
                    _viewModel.SuggestNextQuestionCommand.Execute(null);
                }
            },
            () =>
            {
                if (_viewModel.SuggestReplyCommand.CanExecute(null))
                {
                    _viewModel.SuggestReplyCommand.Execute(null);
                }
            },
            question =>
            {
                // The overlay has its own input: hand its text to the view model before asking.
                if (!string.IsNullOrWhiteSpace(question))
                {
                    _viewModel.OverlayPrompt = question;
                }

                if (_viewModel.WhatShouldISayCommand.CanExecute(null))
                {
                    _viewModel.WhatShouldISayCommand.Execute(null);
                }
            },
            () =>
            {
                if (_viewModel.FollowUpQuestionCommand.CanExecute(null))
                {
                    _viewModel.FollowUpQuestionCommand.Execute(null);
                }
            },
            () =>
            {
                if (_viewModel.RecapCommand.CanExecute(null))
                {
                    _viewModel.RecapCommand.Execute(null);
                }
            },
            enabled => _viewModel.SmartModeEnabled = enabled);
        _assistOverlayWindow.SetSmartMode(_viewModel.SmartModeEnabled);

        _viewModel.ToggleOverlayRequested += ToggleAssistOverlay;
        _viewModel.ShowOverlayRequested += ShowAssistOverlay;
        _viewModel.HideOverlayRequested += HideAssistOverlay;
        _viewModel.WindowModeRequested += ApplyWindowMode;
        _viewModel.HotkeysChanged += RegisterHotkeys;
        _viewModel.OverlayCaptureStateChanged += OnOverlayCaptureStateChanged;
        _viewModel.OverlaySuggestionChanged += text => Dispatcher.Invoke(() => _assistOverlayWindow.SetSuggestionPreview(text));
        _viewModel.OverlayQuestionAsked += text => Dispatcher.Invoke(() => _assistOverlayWindow.AddUserQuestion(text));
        _viewModel.OverlayAnswerFailed += () => Dispatcher.Invoke(() => _assistOverlayWindow.HideThinking());
        _viewModel.OverlayStatsChanged += text => Dispatcher.Invoke(() => _assistOverlayWindow.SetStats(text));
        _viewModel.OverlayNoticeRequested += text => Dispatcher.Invoke(() => _assistOverlayWindow.ShowNotice(text));
        _viewModel.MainWindowAttentionRequested += FocusMainWindowAfterAsk;
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.OverlayPrompt))
            {
                Dispatcher.Invoke(() => _assistOverlayWindow.OverlayPrompt = _viewModel.OverlayPrompt);
            }

            if (e.PropertyName == nameof(MainViewModel.SmartModeEnabled))
            {
                Dispatcher.Invoke(() => _assistOverlayWindow.SetSmartMode(_viewModel.SmartModeEnabled));
            }

            if (e.PropertyName == nameof(MainViewModel.ShowInScreenShare))
            {
                Dispatcher.Invoke(() => ScreenShareProtection.SetShowInScreenShare(_viewModel.ShowInScreenShare));
            }
        };
        _viewModel.OverlayMoveRequested += (dx, dy) => Dispatcher.Invoke(() => MoveOverlay(dx, dy));
        _viewModel.PermissionConfirmationRequested += ShowPermissionWizard;
        _viewModel.ConfirmationRequested += ShowConfirmationDialog;
        _viewModel.NotificationRequested += ShowNotification;
        InitializeLibraryUi();

        _indicatorWindow.HideRequested += HideAssistOverlay;
        _indicatorWindow.AskRequested += ShowAssistOverlay;
        _indicatorWindow.ToggleCaptureRequested += () =>
        {
            if (_viewModel.ToggleCaptureCommand.CanExecute(null))
            {
                _viewModel.ToggleCaptureCommand.Execute(null);
            }
        };
        _indicatorWindow.SnapshotRequested += () =>
        {
            if (_viewModel.CaptureSnapshotCommand.CanExecute(null))
            {
                _viewModel.CaptureSnapshotCommand.Execute(null);
            }
        };
        _indicatorWindow.OpenMainRequested += OpenMainHome;

        DataContext = _viewModel;
        // Bindings with StringFormat (dates, minutes, KB/s) follow the UI language instead of en-US.
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(System.Globalization.CultureInfo.CurrentUICulture.IetfLanguageTag);
        InitializeComponent();
        // Needs the named XAML elements (graph view, question box), so it runs after InitializeComponent.
        InitializeTopicWorkspaceUi();
        InitializeTrayIcon();
        Deactivated += (_, _) => _deactivatedAt = DateTime.UtcNow;
        if (Application.Current != null)
        {
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ScreenShareProtection.Apply(this);
        _source =(HwndSource)PresentationSource.FromVisual(this);
        _source.AddHook(WndProc);
        _hotkeyManager = new HotkeyManager(new WindowInteropHelper(this).Handle);
        RegisterHotkeys();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey)
        {
            _viewModel.HandleHotkey(wParam.ToInt32());
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void RegisterHotkeys()
    {
        if (_hotkeyManager == null)
        {
            return;
        }

        _hotkeyManager.UnregisterAll();

        if (!_viewModel.TryBuildHotkeyMap(out Dictionary<int, HotkeyGesture> hotkeys, out List<string> errors))
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

            if (TryGetMovementFallback(pair.Key, out HotkeyGesture fallbackGesture) &&
                _hotkeyManager.Register(pair.Key, fallbackGesture))
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

    private static bool TryGetMovementFallback(int hotkeyId, out HotkeyGesture gesture)
    {
        gesture = null!;
        string? fallbackText = hotkeyId switch
        {
            HotkeyIds.MoveLeft => "Alt+Left",
            HotkeyIds.MoveRight => "Alt+Right",
            HotkeyIds.MoveUp => "Alt+Up",
            HotkeyIds.MoveDown => "Alt+Down",
            _ => null
        };

        if (string.IsNullOrWhiteSpace(fallbackText))
        {
            return false;
        }

        if (!HotkeyGesture.TryParse(fallbackText, out HotkeyGesture? parsed, out _))
        {
            return false;
        }

        gesture = parsed!;
        return true;
    }

    private bool ShowPermissionWizard()
    {
        var wizard = new PermissionWizardWindow(_viewModel.L) { Owner = this };
        return wizard.ShowDialog() == true;
    }

    private void ShowNotification(string title, string message, bool isError)
    {
        Dispatcher.Invoke(() => EchoDialog.Notify(this, _viewModel.L, title, message, isError));
    }

    /// <summary>Every confirmation in the app is a delete, so the dialog uses the red confirm button.</summary>
    private bool ShowConfirmationDialog(string title, string message)
    {
        return Dispatcher.Invoke(() => EchoDialog.Confirm(this, _viewModel.L, title, message, destructive: true));
    }

    private void ApplyWindowMode(string mode)
    {
        switch (mode)
        {
            case "Mini":
                ShowAssistOverlay();
                break;
            case "Hidden":
                HideAssistOverlay();
                Hide();
                break;
            default:
                HideAssistOverlay();
                if (!IsVisible)
                {
                    Show();
                }

                Width = 1040;
                Height = 700;
                break;
        }
    }

    private void ToggleAssistOverlay()
    {
        if (_assistOverlayWindow.IsVisible)
        {
            HideAssistOverlay();
        }
        else
        {
            ShowAssistOverlay();
        }
    }

    /// <summary>Pages fade and rise into place instead of switching abruptly.</summary>
    private void MainPages_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Inner tab controls (session tabs) bubble the same event: only animate the page switch itself.
        if (!ReferenceEquals(e.OriginalSource, MainPages) || !SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(260));
        var shift = new System.Windows.Media.TranslateTransform();
        MainPages.RenderTransform = shift;
        MainPages.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        shift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(10, 0, duration) { EasingFunction = ease });
    }

    private void ShowAssistOverlay()
    {
        if (_assistOverlayWindow.Owner == null)
        {
            _assistOverlayWindow.Owner = this;
        }

        if (!_assistOverlayWindow.IsVisible)
        {
            _assistOverlayWindow.Show();
        }

        if (!_indicatorWindow.IsVisible)
        {
            _indicatorWindow.Show();
        }

        if (!_auxWindowsPositioned)
        {
            PositionAuxWindows();
            _auxWindowsPositioned = true;
        }
        _assistOverlayWindow.Topmost = true;
        _indicatorWindow.Topmost = true;
        _indicatorWindow.SetOverlayHidden(false);
    }

    private void HideAssistOverlay()
    {
        if (_assistOverlayWindow.IsVisible)
        {
            _assistOverlayWindow.Hide();
        }

        if (_indicatorWindow.IsVisible)
        {
            _indicatorWindow.SetOverlayHidden(true);
        }
    }

    private void OnOverlayCaptureStateChanged(bool isActive)
    {
        Dispatcher.Invoke(() =>
        {
            _assistOverlayWindow.SetCaptureStatus(isActive);
            _indicatorWindow.SetCaptureState(isActive);

            if (isActive)
            {
                _assistOverlayWindow.ResetConversation();
                if (!_indicatorWindow.IsVisible)
                {
                    _indicatorWindow.Show();
                }

                if (_assistOverlayWindow.IsVisible)
                {
                    _assistOverlayWindow.Hide();
                }

                if (!_auxWindowsPositioned)
                {
                    PositionAuxWindows();
                    _auxWindowsPositioned = true;
                }

                _indicatorWindow.Topmost = true;
                _indicatorWindow.SetOverlayHidden(true);
                WindowState = WindowState.Minimized;
            }
            else
            {
                HideAssistOverlay();
                if (_indicatorWindow.IsVisible)
                {
                    _indicatorWindow.Hide();
                }

                WindowState = WindowState.Normal;
                Show();
                Activate();
            }
        });
    }

    private void PositionAuxWindows()
    {
        var area = SystemParameters.WorkArea;
        _assistOverlayWindow.Left = area.Left + (area.Width - _assistOverlayWindow.Width) / 2;
        _assistOverlayWindow.Top = area.Bottom - _assistOverlayWindow.Height - 110;
        _indicatorWindow.Left = area.Left + (area.Width - _indicatorWindow.Width) / 2;
        _indicatorWindow.Top = area.Bottom - _indicatorWindow.Height - 28;
    }

    /// <summary>CuePilot was started again while running: show this window instead.</summary>
    public void BringToFront()
    {
        RestoreFromTray();
    }

    /// <summary>
    /// The CuePilot icon of the floating windows works as a switch: a second click folds the window it opened.
    /// The click itself takes the focus from this window, so «in front» also means «was active a moment ago».
    /// </summary>
    private bool FoldIfInFront()
    {
        bool inFront = IsVisible &&
            WindowState != WindowState.Minimized &&
            (IsActive || DateTime.UtcNow - _deactivatedAt < TimeSpan.FromSeconds(1.5));
        if (inFront)
        {
            WindowState = WindowState.Minimized;
        }

        return inFront;
    }

    private void OpenMainHome()
    {
        Dispatcher.Invoke(() =>
        {
            if (FoldIfInFront())
            {
                return;
            }

            if (_viewModel.IsCapturing && _viewModel.OpenActiveSessionCommand.CanExecute(null))
            {
                _viewModel.OpenActiveSessionCommand.Execute(null);
            }
            else
            {
                _viewModel.GoHomeCommand.Execute(null);
            }

            RestoreFromTray();
        });
    }

    private void MoveOverlay(int dx, int dy)
    {
        Rect area = SystemParameters.WorkArea;
        if (_assistOverlayWindow.IsVisible)
        {
            double nextLeft = Math.Clamp(_assistOverlayWindow.Left + dx, area.Left, area.Right - _assistOverlayWindow.Width);
            double nextTop = Math.Clamp(_assistOverlayWindow.Top + dy, area.Top, area.Bottom - _assistOverlayWindow.Height);
            _assistOverlayWindow.Left = nextLeft;
            _assistOverlayWindow.Top = nextTop;
        }

        if (_indicatorWindow.IsVisible)
        {
            double nextIndicatorLeft = Math.Clamp(_indicatorWindow.Left + dx, area.Left, area.Right - _indicatorWindow.Width);
            double nextIndicatorTop = Math.Clamp(_indicatorWindow.Top + dy, area.Top, area.Bottom - _indicatorWindow.Height);
            _indicatorWindow.Left = nextIndicatorLeft;
            _indicatorWindow.Top = nextIndicatorTop;
        }
    }

    private void SessionTitleTextBlock_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.StartRenameSessionCommand.CanExecute(null))
        {
            _viewModel.StartRenameSessionCommand.Execute(null);
            Dispatcher.BeginInvoke(() =>
            {
                SessionTitleEditTextBox.Focus();
                SessionTitleEditTextBox.SelectAll();
            });
        }
    }

    private void SessionTitleEditTextBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitSessionTitleRename();
            MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _viewModel.CancelRenameSessionTitle();
            MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }
    }

    private void SessionTitleEditTextBox_OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_viewModel.IsEditingSessionTitle)
        {
            return;
        }

        if (e.NewFocus is FrameworkElement element && element == SessionTitleTextBlock)
        {
            return;
        }

        CommitSessionTitleRename();
    }

    private void CommitSessionTitleRename()
    {
        if (_viewModel.SaveSessionTitleCommand.CanExecute(null))
        {
            _viewModel.SaveSessionTitleCommand.Execute(null);
        }
    }

    private void FocusMainWindowAfterAsk()
    {
        Dispatcher.Invoke(() =>
        {
            WindowState = WindowState.Normal;
            Show();
            Activate();
        });
    }

    private void InitializeTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_viewModel.L["TrayOpen"], null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
        menu.Items.Add(_viewModel.L["TrayExit"], null, (_, _) => Dispatcher.Invoke(ExitFromTray));

        System.Drawing.Icon trayIcon = SystemIcons.Application;
        try
        {
            // The tray wants the small (16/20/24 px) frame; without a size Icon picks 32 px and Windows blurs it down.
            System.Drawing.Size traySize = Forms.SystemInformation.SmallIconSize;
            string iconPath = Path.Combine(AppContext.BaseDirectory, "cuepilot-favicon.ico");
            if (File.Exists(iconPath))
            {
                trayIcon = new System.Drawing.Icon(iconPath, traySize);
            }
            else
            {
                string projectIconPath = Path.Combine(_projectRoot, "apps", "CuePilotDesktop", "cuepilot-favicon.ico");
                if (File.Exists(projectIconPath))
                {
                    trayIcon = new System.Drawing.Icon(projectIconPath, traySize);
                }
                else
                {
                    string? exePath = Environment.ProcessPath;
                    if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
                    {
                        System.Drawing.Icon? extracted = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                        if (extracted != null)
                        {
                            trayIcon = extracted;
                        }
                    }
                }
            }
        }
        catch
        {
            trayIcon = SystemIcons.Application;
        }

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "CuePilot",
            Icon = trayIcon,
            Visible = true,
            ContextMenuStrip = menu
        };

        _notifyIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    private void ExitFromTray()
    {
        _allowRealClose = true;
        _notifyIcon?.Dispose();
        _notifyIcon = null;
        _assistOverlayWindow.Close();
        _indicatorWindow.Close();
        Close();
        Application.Current?.Shutdown();
    }

    private void SessionAskTextBox_OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        ClearSessionAskPlaceholder();
    }

    private void SessionAskTextBox_OnGotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        ClearSessionAskPlaceholder();
    }

    private void ClearSessionAskPlaceholder()
    {
        string placeholder = _viewModel.L["OverlayPrompt"];
        if (string.IsNullOrWhiteSpace(placeholder))
        {
            return;
        }

        string current = _viewModel.OverlayPrompt?.Trim() ?? string.Empty;
        if (string.Equals(current, placeholder, StringComparison.OrdinalIgnoreCase))
        {
            _viewModel.OverlayPrompt = string.Empty;
        }
    }

    private void ModePromptTextBox_OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        string current = textBox.Text?.Trim() ?? string.Empty;
        if (current.StartsWith("Пример:", StringComparison.OrdinalIgnoreCase))
        {
            textBox.Clear();
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_allowRealClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        HideAssistOverlay();
        if (_indicatorWindow.IsVisible)
        {
            _indicatorWindow.Hide();
        }

        ShowInTaskbar = false;
        Hide();
    }

    protected override void OnClosed(EventArgs e)
    {
        _notifyIcon?.Dispose();
        _notifyIcon = null;
        _viewModel.Shutdown();
        _visualContextCapture.Dispose();
        _hotkeyManager?.UnregisterAll();

        if (_assistOverlayWindow.IsVisible)
        {
            _assistOverlayWindow.Close();
        }

        if (_indicatorWindow.IsVisible)
        {
            _indicatorWindow.Close();
        }

        if (_source != null)
        {
            _source.RemoveHook(WndProc);
        }

        base.OnClosed(e);
    }

    /// <summary>
    /// Repository root when running from source (bin/... below apps/CuePilotDesktop);
    /// the install folder when running an installed copy (DesktopCapture.exe, tools/ and models/ next to the exe).
    /// </summary>
    private static string ResolveProjectRoot()
    {
        string baseDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(baseDir);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "apps", "DesktopCapture");
            if (Directory.Exists(candidate))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return baseDir;
    }
}