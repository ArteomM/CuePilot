using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using CuePilot.Avalonia.Platform;
using CuePilot.Avalonia.Views.Dialogs;
using CuePilotDesktop.Services;
using CuePilotDesktop.ViewModels;

namespace CuePilot.Avalonia.Views;

/// <summary>
/// Main window: the same pages as on Windows (Home with topics, a session, modes, settings).
/// Library and topic workspace behaviour live in MainWindow.Library.cs and MainWindow.TopicWorkspace.cs.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = null!;
    private readonly IVisualContextCapture _visualContextCapture = null!;
    private readonly MiniOverlayWindow _assistOverlayWindow = null!;
    private readonly IndicatorWindow _indicatorWindow = null!;
    private TrayIcon? _trayIcon;
    private bool _allowRealClose;
    private DateTime _deactivatedAt = DateTime.MinValue;
    private bool _auxWindowsPositioned;
    private string _shownPage = string.Empty;

    // Design-time and XAML loader constructor.
    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(string projectRoot)
        : this()
    {
        var settingsService = new JsonSettingsService();
        AppSettings settings = settingsService.Load();

        var localizationService = new LocalizationService();
        localizationService.SetLanguage(settings.UiLanguage);

        var diagnostics = new DiagnosticsViewModel(new EnvironmentDiagnosticsService(projectRoot));
        var captureOrchestrator = new CaptureOrchestratorService();
        _visualContextCapture = new MacVisualContextCapture();

        _assistOverlayWindow = new MiniOverlayWindow(new OverlayPositionStore(), localizationService);
        _indicatorWindow = new IndicatorWindow(localizationService);
        _viewModel = new MainViewModel(projectRoot, captureOrchestrator, _visualContextCapture, diagnostics, settingsService, localizationService);
        _assistOverlayWindow.ConfigureActions(
            () =>
            {
                if (FoldIfInFront())
                {
                    return;
                }

                Execute(_viewModel.OpenActiveSessionCommand);
                RestoreFromTray();
            },
            () => Execute(_viewModel.SuggestNextQuestionCommand),
            () => Execute(_viewModel.SuggestReplyCommand),
            question =>
            {
                // The overlay has its own input: hand its text to the view model before asking.
                if (!string.IsNullOrWhiteSpace(question))
                {
                    _viewModel.OverlayPrompt = question;
                }

                Execute(_viewModel.WhatShouldISayCommand);
            },
            () => Execute(_viewModel.FollowUpQuestionCommand),
            () => Execute(_viewModel.RecapCommand),
            enabled => _viewModel.SmartModeEnabled = enabled);
        _assistOverlayWindow.SetSmartMode(_viewModel.SmartModeEnabled);
        _assistOverlayWindow.HotkeyHint = _viewModel.ToggleOverlayHotkeyText;

        _viewModel.ToggleOverlayRequested += ToggleAssistOverlay;
        _viewModel.ShowOverlayRequested += ShowAssistOverlay;
        _viewModel.HideOverlayRequested += HideAssistOverlay;
        _viewModel.WindowModeRequested += ApplyWindowMode;
        _viewModel.OverlayCaptureStateChanged += OnOverlayCaptureStateChanged;
        _viewModel.OverlaySuggestionChanged += text => OnUi(() => _assistOverlayWindow.SetSuggestionPreview(text));
        _viewModel.OverlayQuestionAsked += text => OnUi(() => _assistOverlayWindow.AddUserQuestion(text));
        _viewModel.OverlayAnswerFailed += () => OnUi(() => _assistOverlayWindow.HideThinking());
        _viewModel.OverlayStatsChanged += text => OnUi(() => _assistOverlayWindow.SetStats(text));
        _viewModel.OverlayNoticeRequested += text => OnUi(() => _assistOverlayWindow.ShowNotice(text));
        _viewModel.MainWindowAttentionRequested += FocusMainWindowAfterAsk;
        _viewModel.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.OverlayPrompt):
                    OnUi(() => _assistOverlayWindow.OverlayPrompt = _viewModel.OverlayPrompt);
                    break;
                case nameof(MainViewModel.SmartModeEnabled):
                    OnUi(() => _assistOverlayWindow.SetSmartMode(_viewModel.SmartModeEnabled));
                    break;
                case nameof(MainViewModel.ToggleOverlayHotkeyText):
                    OnUi(() => _assistOverlayWindow.HotkeyHint = _viewModel.ToggleOverlayHotkeyText);
                    break;
                case nameof(MainViewModel.SelectedMainPage):
                    OnUi(AnimatePageSwitch);
                    break;
                case nameof(MainViewModel.SelectedSessionTab):
                    OnUi(SyncSessionTab);
                    break;
            }
        };
        _viewModel.OverlayMoveRequested += (dx, dy) => OnUi(() => MoveOverlay(dx, dy));
        if (OperatingSystem.IsMacOS())
        {
            // macOS reports its permissions itself: they are checked before each recording, no wizard.
            _viewModel.PlatformPermissionCheckRequested += () => Dispatcher.UIThread.Invoke(CheckMacPermissions);
        }
        else
        {
            _viewModel.PermissionConfirmationRequested += () => Dispatcher.UIThread.Invoke(() => PermissionWizardWindow.Run(this, _viewModel.L));
        }
        _viewModel.ConfirmationRequested += ShowConfirmationDialog;
        _viewModel.NotificationRequested += ShowNotification;

        _indicatorWindow.HideRequested += HideAssistOverlay;
        _indicatorWindow.AskRequested += ShowAssistOverlay;
        _indicatorWindow.ToggleCaptureRequested += () => Execute(_viewModel.ToggleCaptureCommand);
        _indicatorWindow.SnapshotRequested += () => Execute(_viewModel.CaptureSnapshotCommand);
        _indicatorWindow.OpenMainRequested += OpenMainHome;

        DataContext = _viewModel;
        _shownPage = _viewModel.SelectedMainPage;
        SessionTabs.SelectionChanged += SessionTabs_OnSelectionChanged;
        SyncSessionTab();
        InitializeLibraryUi();
        InitializeTopicWorkspaceUi();
        InitializeTrayIcon();
        InitializeMacIntegration(settings);
        Deactivated += (_, _) => _deactivatedAt = DateTime.UtcNow;
        SessionTitleTextBlock.PointerPressed += SessionTitleTextBlock_OnPointerPressed;
        SessionTitleEditTextBox.KeyDown += SessionTitleEditTextBox_OnKeyDown;
        SessionTitleEditTextBox.LostFocus += SessionTitleEditTextBox_OnLostFocus;
        SessionAskTextBox.PointerEntered += (_, _) => ClearSessionAskPlaceholder();
        SessionAskTextBox.GotFocus += (_, _) => ClearSessionAskPlaceholder();
        ModeRealtimePromptTextBox.GotFocus += ModePromptTextBox_OnGotFocus;
        ModeNotesTemplateTextBox.GotFocus += ModePromptTextBox_OnGotFocus;
    }

    /// <summary>CuePilot was started again while running: show this window instead.</summary>
    public void BringToFront()
    {
        RestoreFromTray();
    }

    private static void Execute(System.Windows.Input.ICommand command)
    {
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    private void ShowNotification(string title, string message, bool isError)
    {
        Dispatcher.UIThread.Invoke(() => EchoDialog.Notify(this, _viewModel.L, title, message, isError));
    }

    /// <summary>Every confirmation in the app is a delete, so the dialog uses the red confirm button.</summary>
    private bool ShowConfirmationDialog(string title, string message)
    {
        return Dispatcher.UIThread.Invoke(() => EchoDialog.Confirm(this, _viewModel.L, title, message, destructive: true));
    }

    private void ApplyWindowMode(string mode)
    {
        OnUi(() =>
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
        });
    }

    private void ToggleAssistOverlay()
    {
        OnUi(() =>
        {
            if (_assistOverlayWindow.IsVisible)
            {
                HideAssistOverlay();
            }
            else
            {
                ShowAssistOverlay();
            }
        });
    }

    /// <summary>Pages fade and rise into place instead of switching abruptly.</summary>
    private void AnimatePageSwitch()
    {
        if (_shownPage == _viewModel.SelectedMainPage)
        {
            return;
        }

        _shownPage = _viewModel.SelectedMainPage;
        var shift = new TranslateTransform();
        MainPages.RenderTransform = shift;
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(260),
            Easing = new CubicEaseOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0d), new Setter(TranslateTransform.YProperty, 10d) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1d), new Setter(TranslateTransform.YProperty, 0d) } }
            }
        };
        _ = animation.RunAsync(MainPages);
    }

    /// <summary>Session tabs carry their name in Tag, like SelectedValuePath="Tag" on Windows.</summary>
    private void SyncSessionTab()
    {
        foreach (object? item in SessionTabs.Items)
        {
            if (item is TabItem { Tag: string tag } tab && tag == _viewModel.SelectedSessionTab)
            {
                SessionTabs.SelectedItem = tab;
                return;
            }
        }
    }

    private void SessionTabs_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, SessionTabs) && SessionTabs.SelectedItem is TabItem { Tag: string tag } && tag != _viewModel.SelectedSessionTab)
        {
            _viewModel.SelectedSessionTab = tag;
        }
    }

    private void ShowAssistOverlay()
    {
        OnUi(() =>
        {
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
        });
    }

    private void HideAssistOverlay()
    {
        OnUi(() =>
        {
            if (_assistOverlayWindow.IsVisible)
            {
                _assistOverlayWindow.Hide();
            }

            if (_indicatorWindow.IsVisible)
            {
                _indicatorWindow.SetOverlayHidden(true);
            }
        });
    }

    private void OnOverlayCaptureStateChanged(bool isActive)
    {
        OnUi(() =>
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

    /// <summary>Working area of the screen in window units (DIPs).</summary>
    private Rect WorkArea()
    {
        Screen? screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null)
        {
            return new Rect(0, 0, 1440, 900);
        }

        PixelRect area = screen.WorkingArea;
        double scale = screen.Scaling;
        return new Rect(area.X / scale, area.Y / scale, area.Width / scale, area.Height / scale);
    }

    private static void Place(Window window, double left, double top)
    {
        double scale = window.Screens.ScreenFromWindow(window)?.Scaling ?? window.Screens.Primary?.Scaling ?? 1;
        window.Position = new PixelPoint((int)(left * scale), (int)(top * scale));
    }

    private static Point PlaceOf(Window window)
    {
        double scale = window.Screens.ScreenFromWindow(window)?.Scaling ?? window.Screens.Primary?.Scaling ?? 1;
        return new Point(window.Position.X / scale, window.Position.Y / scale);
    }

    private void PositionAuxWindows()
    {
        Rect area = WorkArea();
        Place(_assistOverlayWindow, area.Left + (area.Width - _assistOverlayWindow.Width) / 2, area.Bottom - _assistOverlayWindow.Height - 110);
        Place(_indicatorWindow, area.Left + (area.Width - _indicatorWindow.Width) / 2, area.Bottom - _indicatorWindow.Height - 28);
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
        OnUi(() =>
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
        Rect area = WorkArea();
        foreach (Window window in new Window[] { _assistOverlayWindow, _indicatorWindow })
        {
            if (!window.IsVisible)
            {
                continue;
            }

            Point at = PlaceOf(window);
            double width = window.Bounds.Width > 0 ? window.Bounds.Width : window.Width;
            double height = window.Bounds.Height > 0 ? window.Bounds.Height : window.Height;
            Place(window,
                Math.Clamp(at.X + dx, area.Left, Math.Max(area.Left, area.Right - width)),
                Math.Clamp(at.Y + dy, area.Top, Math.Max(area.Top, area.Bottom - height)));
        }
    }

    private void SessionTitleTextBlock_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel.StartRenameSessionCommand.CanExecute(null))
        {
            _viewModel.StartRenameSessionCommand.Execute(null);
            Dispatcher.UIThread.Post(() =>
            {
                SessionTitleEditTextBox.Focus();
                SessionTitleEditTextBox.SelectAll();
            }, DispatcherPriority.Input);
        }
    }

    private void SessionTitleEditTextBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitSessionTitleRename();
            FocusManager?.ClearFocus();
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _viewModel.CancelRenameSessionTitle();
            FocusManager?.ClearFocus();
        }
    }

    private void SessionTitleEditTextBox_OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.IsEditingSessionTitle)
        {
            CommitSessionTitleRename();
        }
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
        OnUi(() =>
        {
            WindowState = WindowState.Normal;
            Show();
            Activate();
        });
    }

    /// <summary>The tray icon of Windows; on macOS it is the icon in the menu bar.</summary>
    private void InitializeTrayIcon()
    {
        var menu = new NativeMenu();
        var open = new NativeMenuItem(_viewModel.L["TrayOpen"]);
        open.Click += (_, _) => OnUi(RestoreFromTray);
        var exit = new NativeMenuItem(_viewModel.L["TrayExit"]);
        exit.Click += (_, _) => OnUi(ExitFromTray);
        menu.Items.Add(open);
        menu.Items.Add(exit);

        _trayIcon = new TrayIcon
        {
            ToolTipText = "CuePilot",
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://CuePilot/Assets/cuepilot-icon.png"))),
            Menu = menu,
            IsVisible = true
        };
        _trayIcon.Clicked += (_, _) => OnUi(RestoreFromTray);
        if (Application.Current is { } app)
        {
            TrayIcon.SetIcons(app, [_trayIcon]);
        }
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
        if (_trayIcon != null)
        {
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _assistOverlayWindow.Close();
        _indicatorWindow.Close();
        Close();
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
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

    private void ModePromptTextBox_OnGotFocus(object? sender, GotFocusEventArgs e)
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

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Quit from the app menu (⌘Q), the Dock or a system shutdown really quits.
        if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
        {
            _allowRealClose = true;
        }

        if (_allowRealClose || _viewModel == null)
        {
            base.OnClosing(e);
            return;
        }

        // Closing the window keeps CuePilot running in the menu bar, as the tray does on Windows.
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
        _trayIcon?.Dispose();
        _trayIcon = null;
        if (_viewModel != null)
        {
            _viewModel.Shutdown();
            (_visualContextCapture as IDisposable)?.Dispose();
            _assistOverlayWindow.Close();
            _indicatorWindow.Close();
        }

        base.OnClosed(e);
    }

    /// <summary>
    /// Repository root when running from source (bin/... below apps/CuePilot.Avalonia);
    /// the app folder when running an installed copy (DesktopCapture, tools/ and models/ next to the executable).
    /// </summary>
    public static string ResolveProjectRoot()
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
