using System.IO;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using Forms = global::System.Windows.Forms;
using ReplioDesktop.Services;
using ReplioDesktop.ViewModels;
using ReplioDesktop.Views;

namespace ReplioDesktop;

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
    private DateTime _auxPressAt = DateTime.MinValue;
    private bool _inFrontAtAuxPress;
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
        // Back from the personal account in the browser: pick up a new plan without waiting for the timer.
        Activated += (_, _) => _viewModel.RefreshAccountIfStale(120);
        // During a recording the main window comes back over the floating bar: move the bar off its input row.
        Activated += (_, _) => Dispatcher.BeginInvoke(new Action(KeepIndicatorClearOfMainWindow), System.Windows.Threading.DispatcherPriority.Background);
        _viewModel.HotkeysChanged += RegisterHotkeys;
        _viewModel.ExitForUpdateRequested += () => Dispatcher.BeginInvoke(new Action(ExitFromTray));
        _viewModel.RestartRequested += () => Dispatcher.BeginInvoke(new Action(RestartApp));
        _viewModel.OverlayCaptureStateChanged += OnOverlayCaptureStateChanged;
        _viewModel.OverlaySuggestionChanged += text => Dispatcher.Invoke(() => _assistOverlayWindow.SetSuggestionPreview(text));
        _viewModel.OverlayQuestionAsked += text => Dispatcher.Invoke(() => _assistOverlayWindow.AddUserQuestion(text));
        _viewModel.OverlayAnswerFailed += () => Dispatcher.Invoke(() => _assistOverlayWindow.HideThinking());
        _viewModel.OverlayStatsChanged += text => Dispatcher.Invoke(() => _assistOverlayWindow.SetStats(text));
        _viewModel.OverlayNoticeRequested += text => Dispatcher.Invoke(() =>
        {
            _assistOverlayWindow.ShowNotice(text);
            if (!_assistOverlayWindow.IsVisible)
            {
                // During a call the overlay is usually hidden: the recording indicator shows the message instead.
                _indicatorWindow.ShowNotice(text);
            }
        });
        _viewModel.SnapshotStageChanged += (stage, number) => Dispatcher.Invoke(() => _indicatorWindow.SetSnapshotStage(stage, number));
        void ShowLimitOnIndicator() => _indicatorWindow.SetLimit(_viewModel.IsLimitLow, _viewModel.LimitLeftPercent, _viewModel.LimitBarColorOnDark, _viewModel.LimitBarTooltip);
        _viewModel.LimitChanged += () => Dispatcher.Invoke(ShowLimitOnIndicator);
        ShowLimitOnIndicator();
        _assistOverlayWindow.HotkeyHint = _viewModel.ToggleOverlayHotkeyText;
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

            if (e.PropertyName == nameof(MainViewModel.ToggleOverlayHotkeyText))
            {
                Dispatcher.Invoke(() => _assistOverlayWindow.HotkeyHint = _viewModel.ToggleOverlayHotkeyText);
            }

            if (e.PropertyName == nameof(MainViewModel.ShowInScreenShare))
            {
                Dispatcher.Invoke(() => ScreenShareProtection.SetShowInScreenShare(_viewModel.ShowInScreenShare));
            }

            if (e.PropertyName == nameof(MainViewModel.SettingsSection))
            {
                // Each settings section opens from its top.
                Dispatcher.BeginInvoke(() => SettingsScroll.ScrollToTop());
            }
        };
        _viewModel.OverlayMoveRequested += (dx, dy) => Dispatcher.Invoke(() => MoveOverlay(dx, dy));
        _viewModel.PermissionConfirmationRequested += ShowPermissionWizard;
        _viewModel.ConfirmationRequested += ShowConfirmationDialog;
        _viewModel.NotificationRequested += ShowNotification;
        _viewModel.HelpWindowRequested += () => Views.HelpWindow.ShowFor(this, _viewModel);
        InitializeLibraryUi();

        _indicatorWindow.HideRequested += HideAssistOverlay;
        _indicatorWindow.AskRequested += ShowAssistOverlay;
        // A press on a floating window takes the focus from this one: remember first whether this window was in front.
        _indicatorWindow.PreviewMouseLeftButtonDown += (_, _) => RememberFrontStateAtAuxPress();
        _assistOverlayWindow.PreviewMouseLeftButtonDown += (_, _) => RememberFrontStateAtAuxPress();
        // «Спросить»/«Скрыть» follows the overlay itself, however it was shown or hidden.
        _assistOverlayWindow.IsVisibleChanged += (_, _) => _indicatorWindow.SetOverlayHidden(!_assistOverlayWindow.IsVisible);
        _indicatorWindow.ToggleCaptureRequested += () =>
        {
            if (_viewModel.ToggleCaptureCommand.CanExecute(null))
            {
                _viewModel.ToggleCaptureCommand.Execute(null);
            }
        };
        // Not through the command: with screenshots off it cannot run and the button would stay silent.
        _indicatorWindow.SnapshotRequested += _viewModel.TakeSnapshot;
        _indicatorWindow.OverlayShown = () => _assistOverlayWindow.IsVisible;
        _indicatorWindow.OpenMainRequested += OpenMainHome;

        DataContext = _viewModel;
        // Bindings with StringFormat (dates, minutes, KB/s) follow the UI language instead of en-US.
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(System.Globalization.CultureInfo.CurrentUICulture.IetfLanguageTag);
        InitializeComponent();
        // Needs the named XAML elements (graph view, question box), so it runs after InitializeComponent.
        InitializeTopicWorkspaceUi();
        InitializeTrayIcon();
        Deactivated += (_, _) => _deactivatedAt = DateTime.UtcNow;
        // Search everything: the results list reopens on focus; Enter opens the first result, Esc closes the list.
        SearchBox.GotKeyboardFocus += (_, _) => _viewModel.ReopenSearchResults();
        SearchBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                _viewModel.OpenFirstSearchResult();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                _viewModel.IsSearchPopupOpen = false;
            }
        };
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
        // No owner on purpose: an owned overlay disappeared and came back with the main window, so opening the
        // transcript also opened «Что спросить» and folding the main window hid it.
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
            UpdateTrayForCapture(isActive);

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

    /// <summary>
    /// The floating bar must not cover the main window (it sat on «Новая запись» and the question box): when they
    /// overlap, the bar moves next to the window, below it, or above it, whichever fits on the screen.
    /// </summary>
    private void KeepIndicatorClearOfMainWindow()
    {
        if (!_indicatorWindow.IsVisible || WindowState != WindowState.Normal || !IsVisible)
        {
            return;
        }

        Rect area = SystemParameters.WorkArea;
        var main = new Rect(Left, Top, ActualWidth, ActualHeight);
        double width = _indicatorWindow.ActualWidth > 0 ? _indicatorWindow.ActualWidth : _indicatorWindow.Width;
        double height = _indicatorWindow.ActualHeight > 0 ? _indicatorWindow.ActualHeight : _indicatorWindow.Height;
        if (!main.IntersectsWith(new Rect(_indicatorWindow.Left, _indicatorWindow.Top, width, height)))
        {
            return;
        }

        const double gap = 12;
        double top = Math.Clamp(main.Bottom - height, area.Top, area.Bottom - height);
        System.Windows.Point[] candidates =
        [
            new(main.Right + gap, top),
            new(main.Left - gap - width, top),
            new(main.Left + (main.Width - width) / 2, main.Bottom + gap),
            new(main.Left + (main.Width - width) / 2, main.Top - gap - height)
        ];
        foreach (System.Windows.Point candidate in candidates)
        {
            if (candidate.X >= area.Left && candidate.Y >= area.Top && candidate.X + width <= area.Right && candidate.Y + height <= area.Bottom)
            {
                _indicatorWindow.Left = candidate.X;
                _indicatorWindow.Top = candidate.Y;
                return;
            }
        }
    }

    private void PositionAuxWindows()
    {
        var area = SystemParameters.WorkArea;
        _assistOverlayWindow.Left = area.Left + (area.Width - _assistOverlayWindow.Width) / 2;
        _assistOverlayWindow.Top = area.Bottom - _assistOverlayWindow.Height - 110;
        _indicatorWindow.Left = area.Left + (area.Width - _indicatorWindow.Width) / 2;
        _indicatorWindow.Top = area.Bottom - _indicatorWindow.Height - 28;
    }

    /// <summary>Replio was started again while running: show this window instead.</summary>
    public void BringToFront()
    {
        RestoreFromTray();
    }

    /// <summary>
    /// The Replio icon of the floating windows works as a switch: a second click folds the window it opened.
    /// The click itself takes the focus from this window, so «in front» means «was active when the button was pressed»,
    /// remembered at the press (<see cref="RememberFrontStateAtAuxPress"/>). A fixed «active a moment ago» window used to
    /// fold the main window when the user had just clicked the overlay, so the button had to be pressed twice.
    /// </summary>
    private bool FoldIfInFront()
    {
        bool pressedJustNow = DateTime.UtcNow - _auxPressAt < TimeSpan.FromSeconds(3);
        bool inFront = IsVisible &&
            WindowState != WindowState.Minimized &&
            (IsActive || (pressedJustNow && _inFrontAtAuxPress));
        if (inFront)
        {
            WindowState = WindowState.Minimized;
        }

        return inFront;
    }

    private void RememberFrontStateAtAuxPress()
    {
        _auxPressAt = DateTime.UtcNow;
        _inFrontAtAuxPress = IsVisible &&
            WindowState != WindowState.Minimized &&
            (IsActive || DateTime.UtcNow - _deactivatedAt < TimeSpan.FromMilliseconds(400));
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
        // While a recording runs: «Открыть транскрипт», «Остановить запись», «Снимок экрана»; the icon blinks a red dot.
        var menu = new Forms.ContextMenuStrip();
        _trayOpenItem = (Forms.ToolStripMenuItem)menu.Items.Add(_viewModel.L["TrayOpen"], null, (_, _) => Dispatcher.Invoke(OpenFromTray));
        _trayCaptureItem = (Forms.ToolStripMenuItem)menu.Items.Add(_viewModel.L["TrayStartRecording"], null, (_, _) => Dispatcher.Invoke(() =>
        {
            if (_viewModel.ToggleCaptureCommand.CanExecute(null))
            {
                _viewModel.ToggleCaptureCommand.Execute(null);
            }
        }));
        _traySnapshotItem = (Forms.ToolStripMenuItem)menu.Items.Add(_viewModel.L["TraySnapshot"], null, (_, _) => Dispatcher.Invoke(_viewModel.TakeSnapshot));
        _traySnapshotItem.Enabled = false;
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_viewModel.L["TrayExit"], null, (_, _) => Dispatcher.Invoke(ExitFromTray));

        System.Drawing.Icon trayIcon = SystemIcons.Application;
        try
        {
            // The tray wants the small (16/20/24 px) frame; without a size Icon picks 32 px and Windows blurs it down.
            System.Drawing.Size traySize = Forms.SystemInformation.SmallIconSize;
            string iconPath = Path.Combine(AppContext.BaseDirectory, "replio-favicon.ico");
            if (File.Exists(iconPath))
            {
                trayIcon = new System.Drawing.Icon(iconPath, traySize);
            }
            else
            {
                string projectIconPath = Path.Combine(_projectRoot, "apps", "ReplioDesktop", "replio-favicon.ico");
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

        _trayIdleIcon = trayIcon;
        _trayRecordingIcon = MakeRecordingTrayIcon(trayIcon) ?? trayIcon;
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "Replio",
            Icon = trayIcon,
            Visible = true,
            ContextMenuStrip = menu
        };

        _notifyIcon.DoubleClick += (_, _) => Dispatcher.Invoke(OpenFromTray);
        // One click opens the live transcript during a recording (the tooltip says so).
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left && _viewModel.IsCapturing)
            {
                Dispatcher.Invoke(OpenFromTray);
            }
        };
        _trayBlinkTimer.Tick += (_, _) =>
        {
            if (_notifyIcon == null)
            {
                return;
            }

            _trayBlinkOn = !_trayBlinkOn;
            _notifyIcon.Icon = _trayBlinkOn ? _trayRecordingIcon : _trayIdleIcon;
        };
    }

    private Forms.ToolStripMenuItem? _trayOpenItem;
    private Forms.ToolStripMenuItem? _trayCaptureItem;
    private Forms.ToolStripMenuItem? _traySnapshotItem;
    private System.Drawing.Icon? _trayIdleIcon;
    private System.Drawing.Icon? _trayRecordingIcon;
    private bool _trayBlinkOn;
    private readonly System.Windows.Threading.DispatcherTimer _trayBlinkTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };

    /// <summary>During a recording: the session with its live transcript; otherwise the window as it was.</summary>
    private void OpenFromTray()
    {
        if (_viewModel.IsCapturing && _viewModel.OpenActiveSessionCommand.CanExecute(null))
        {
            _viewModel.OpenActiveSessionCommand.Execute(null);
        }

        RestoreFromTray();
    }

    /// <summary>Menu, tooltip and the blinking icon follow the recording.</summary>
    private void UpdateTrayForCapture(bool isActive)
    {
        if (_notifyIcon == null)
        {
            return;
        }

        if (_trayOpenItem != null)
        {
            _trayOpenItem.Text = _viewModel.L[isActive ? "TrayOpenTranscript" : "TrayOpen"];
        }

        if (_trayCaptureItem != null)
        {
            _trayCaptureItem.Text = _viewModel.L[isActive ? "TrayStopRecording" : "TrayStartRecording"];
        }

        if (_traySnapshotItem != null)
        {
            _traySnapshotItem.Enabled = isActive;
        }

        // NotifyIcon.Text is limited to 127 characters.
        string tip = isActive ? _viewModel.L["TrayTooltipRecording"] : "Replio";
        _notifyIcon.Text = tip.Length > 127 ? tip[..127] : tip;
        if (isActive)
        {
            _trayBlinkOn = true;
            _notifyIcon.Icon = _trayRecordingIcon;
            _trayBlinkTimer.Start();
        }
        else
        {
            _trayBlinkTimer.Stop();
            _trayBlinkOn = false;
            _notifyIcon.Icon = _trayIdleIcon;
        }
    }

    /// <summary>The tray icon with a red «recording» dot in the corner, drawn once.</summary>
    private static System.Drawing.Icon? MakeRecordingTrayIcon(System.Drawing.Icon baseIcon)
    {
        try
        {
            using System.Drawing.Bitmap bitmap = baseIcon.ToBitmap();
            using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                float size = bitmap.Width * 0.5f;
                float x = bitmap.Width - size, y = bitmap.Height - size;
                using var white = new System.Drawing.SolidBrush(System.Drawing.Color.White);
                using var red = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0xFF, 0x4D, 0x4F));
                graphics.FillEllipse(white, x, y, size, size);
                graphics.FillEllipse(red, x + size * 0.15f, y + size * 0.15f, size * 0.7f, size * 0.7f);
            }

            return System.Drawing.Icon.FromHandle(bitmap.GetHicon());
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    /// <summary>Starts a new copy (it waits for this one to close), then quits: used after the data folder changed.</summary>
    private void RestartApp()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--restart") { UseShellExecute = false })?.Dispose();
            }
        }
        catch (Exception)
        {
            // Without the new copy the user starts Replio again by hand; quitting still applies the change.
        }

        ExitFromTray();
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
    /// Repository root when running from source (bin/... below apps/ReplioDesktop);
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