using Android.Content;
using Android.OS;
using Android.Provider;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Replio.Android.Platform;
using Replio.Android.Platform.Calls;

namespace Replio.Android.Views;

/// <summary>
/// Calls on the main screen: a small status in the app bar («● Ждёт звонка», «Настроить звонки», «● 03:12» while
/// recording) and, while steps are left, one slim card on Home; the steps themselves (microphone, «Слушать
/// звонок», not putting Replio to sleep, the phone's call recordings) are in a sheet that scrolls.
/// </summary>
public partial class MainView
{
    private readonly DispatcherTimer _pillTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private bool _wasSignedIn;

    private void SetUpCallCard()
    {
        _wasSignedIn = _viewModel.IsSignedIn;
        CallAssistant.Instance.StateChanged += () => Dispatcher.UIThread.Post(UpdateCallCard);
        // While recording the status in the app bar counts the time.
        _pillTimer.Tick += (_, _) =>
        {
            if (CallAssistant.Instance.IsRecording)
            {
                CallPillText.Text = CallAssistant.Instance.ElapsedText;
            }
            else
            {
                _pillTimer.Stop();
            }
        };
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ReplioDesktop.ViewModels.MainViewModel.IsSignedIn) or nameof(ReplioDesktop.ViewModels.MainViewModel.IsSigningIn))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    // Just signed in on the site: back to Replio without looking for it.
                    bool signedIn = _viewModel.IsSignedIn;
                    if (signedIn && !_wasSignedIn)
                    {
                        MainActivity.Current?.ReturnFromBrowser();
                    }

                    _wasSignedIn = signedIn;
                    UpdateCallCard();
                });
            }

            if (e.PropertyName == nameof(ReplioDesktop.ViewModels.MainViewModel.IsCapturing))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateCallCard();
                    if (_viewModel.IsCapturing)
                    {
                        _pillTimer.Start();
                    }
                });
            }
        };
        // The theme brushes are found only once the view is on screen.
        AttachedToVisualTree += (_, _) => UpdateCallCard();
        UpdateCallCard();
    }

    /// <summary>The card must never take the app down: a failure is only logged.</summary>
    private void UpdateCallCard()
    {
        try
        {
            FillCallCard();
        }
        catch (Exception ex)
        {
            PhonePlatform.Log("[calls] Call card could not be shown: " + ex);
        }
    }

    private IBrush CardBrush(string key, string fallback) => Brush(key) ?? new SolidColorBrush(Color.Parse(fallback));

    private bool _fillingCallCard;

    /// <summary>The «Replio на звонках» switch at the top of the sheet.</summary>
    private void CallWatch_OnChanged(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_fillingCallCard)
        {
            return;
        }

        CallAssistant.Instance.WatchCalls = CallWatchSwitch.IsChecked == true;
        UpdateCallCard();
    }

    private void FillCallCard()
    {
        _fillingCallCard = true;
        try
        {
            FillCallCardCore();
        }
        finally
        {
            _fillingCallCard = false;
        }
    }

    private void FillCallCardCore()
    {
        Context context = global::Android.App.Application.Context;
        bool signedIn = !_viewModel.IsManagedAi || _viewModel.IsSignedIn;
        bool mic = PhonePlatform.HasMicrophonePermission();
        bool listen = ReplioAccessibilityService.IsEnabled(context);
        bool battery = IsIgnoringBatteryOptimizations(context);
        CallAssistant assistant = CallAssistant.Instance;

        CallWatchSwitch.IsChecked = assistant.WatchCalls;
        CallWatchTitle.Text = CallText.Get("SetupWatch");
        CallWatchText.Text = CallText.Get(assistant.WatchCalls ? "SetupWatchOn" : "SetupWatchOff");
        CallSetupList.Opacity = assistant.WatchCalls ? 1 : 0.5;

        // The steps that must be done, numbered; only the next one has the bright button, done ones a tick.
        CallSetupList.Children.Clear();
        int number = 0;
        bool nextTaken = false;
        bool IsNext(bool done)
        {
            if (done || nextTaken)
            {
                return false;
            }

            nextTaken = true;
            return true;
        }

        if (!signedIn)
        {
            // Without signing in nothing becomes text: the first step (the page opens inside Replio and comes back by itself).
            bool waiting = _viewModel.IsSigningIn;
            AddSetupRow(++number, MobileText.Get("StepSignIn"), MobileText.Get(waiting ? "StepSigningIn" : "StepSignInHint"), false, IsNext(false), () =>
            {
                Execute(waiting ? _viewModel.OpenSignInPageCommand : _viewModel.SignInCommand);
            }, MobileText.Get("StepSignInButton"));
        }

        AddSetupRow(++number, CallText.Get("SetupMic"), null, mic, IsNext(mic), async () =>
        {
            await PhonePlatform.RequestMicrophonePermissionAsync();
            UpdateCallCard();
        }, change: () => OpenAppInfo(MainActivity.Current ?? context));

        // Android 13+ blocks the switch for an app installed from a file until «Разрешить ограниченные настройки»:
        // a quiet link under the step, not a step of its own.
        Control? restricted = null;
        if (!listen && Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            restricted = SetupLink(CallText.Get("SetupRestricted"), () => ShowDialog(
                CallText.Get("SetupRestricted"),
                CallText.Get("SetupRestrictedHint"),
                [(MobileText.Get("NotNow"), null, false), (CallText.Get("SetupAppInfo"), () => OpenAppInfo(MainActivity.Current ?? context), false)]));
        }

        AddSetupRow(++number, CallText.Get("SetupListen"), listen ? null : CallText.Get("SetupListenHint"), listen, IsNext(listen),
            ExplainListen,
            change: () => ReplioAccessibilityService.OpenSettings(MainActivity.Current ?? context),
            below: restricted);

        AddSetupRow(++number, CallText.Get("SetupBattery"), null, battery, IsNext(battery), () => RequestIgnoreBatteryOptimizations(MainActivity.Current ?? context),
            change: () => OpenBatterySettings(MainActivity.Current ?? context));

        // Not needed for calls to work: under «Дополнительно».
        CallSetupList.Children.Add(new TextBlock
        {
            Text = MobileText.Get("StepMore"),
            Margin = new global::Avalonia.Thickness(0, 10, 0, -4),
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            Foreground = CardBrush("EchoMutedBrush", "#9AA5B5")
        });
        bool recordings = CallRecordingImporter.HasPermission(context);
        AddSetupRow(0, CallText.Get("SetupRecordings"), recordings ? null : CallText.Get("SetupRecordingsHint"), recordings, false, async () =>
        {
            if (MainActivity.Current is { } activity)
            {
                await activity.RequestPermissionAsync(CallRecordingImporter.Permission);
                // Recordings from now on are picked up, older ones are left alone.
                CallRecordingImporter.MarkNow();
            }

            UpdateCallCard();
        }, change: () => OpenAppInfo(MainActivity.Current ?? context));

        int left = (signedIn ? 0 : 1) + (mic ? 0 : 1) + (listen ? 0 : 1) + (battery ? 0 : 1);
        CallSetupTitle.Text = CallText.Get("SetupTitle");
        CallSetupBanner.IsVisible = left > 0 && !assistant.IsRecording && assistant.WatchCalls;
        CallSetupBannerTitle.Text = MobileText.Get("BannerTitle");
        CallSetupBannerText.Text = string.Format(MobileText.Get("BannerText"), left);
        UpdateCallPillVisibility();

        // The status in the app bar.
        bool recording = assistant.IsRecording || _viewModel.IsCapturing;
        CallPill.Classes.Set("live", recording);
        CallPill.Classes.Set("todo", !recording && left > 0 && assistant.WatchCalls);
        CallPillDot.IsVisible = recording || left == 0 || !assistant.WatchCalls;
        if (recording)
        {
            CallPillText.Text = assistant.IsRecording ? assistant.ElapsedText : MobileText.Get("Recording");
            CallPillDot.Fill = new SolidColorBrush(Color.Parse("#E5484D"));
        }
        else if (!assistant.WatchCalls)
        {
            CallPillText.Text = MobileText.Get("CallsPaused");
            CallPillDot.Fill = CardBrush("EchoMutedBrush", "#9AA5B5");
        }
        else if (left > 0)
        {
            CallPillText.Text = MobileText.Get("CallsSetup");
        }
        else
        {
            CallPillText.Text = MobileText.Get("CallsWaiting");
            CallPillDot.Fill = CardBrush("EchoAccentBrush", "#5EF0C1");
        }

        // The sheet starts with what Replio does now, then the steps.
        CallSetupStatus.Text = left == 0 ? CallText.Get("SetupReady") : CallText.Get("SetupNeeded");
        CallSetupStatus.IsVisible = assistant.WatchCalls;
        CallSpeakerNote.Text = CallText.Get("SetupSpeaker");
    }

    /// <summary>
    /// The calls status in the app bar; not on a session page (it has its own timer) and not on Home while the
    /// setup card there already says the same.
    /// </summary>
    private void UpdateCallPillVisibility()
    {
        string page = _viewModel.SelectedMainPage;
        CallPill.IsVisible = page != "Session" && !(page == "Home" && CallSetupBanner.IsVisible);
    }

    /// <summary>The app bar status: while recording it opens the recording, otherwise the call setup.</summary>
    private void CallPill_OnClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel.IsCapturing)
        {
            Execute(_viewModel.OpenActiveSessionCommand);
            return;
        }

        CallSetup_OnClick(sender, e);
    }

    private void CallSetup_OnClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        UpdateCallCard();
        CallSetupPanel.MaxHeight = Math.Max(320, Bounds.Height * 0.85);
        Motion.Show(CallSetupSheet, CallSetupPanel, Motion.Kind.Sheet);
    }

    private void CloseCallSetup() => Motion.Hide(CallSetupSheet, CallSetupPanel, Motion.Kind.Sheet);

    private void CloseCallSetup_OnClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e) => CloseCallSetup();

    private void CallSetupSheetBackdrop_OnPointerPressed(object? sender, global::Avalonia.Input.PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, CallSetupSheet))
        {
            CloseCallSetup();
        }
    }

    /// <summary>
    /// What the «Слушать звонок» service does and does not do, before Android's own accessibility page (the page
    /// that scares people most).
    /// </summary>
    private void ExplainListen()
    {
        Context context = global::Android.App.Application.Context;
        ShowDialog(
            MobileText.Get("ListenExplainTitle"),
            MobileText.Get("ListenExplainText"),
            [(MobileText.Get("NotNow"), null, false), (MobileText.Get("ListenExplainGo"), () => ReplioAccessibilityService.OpenSettings(MainActivity.Current ?? context), false)]);
    }

    /// <summary>A quiet text link under a step.</summary>
    private Button SetupLink(string text, Action click)
    {
        var link = new Button
        {
            Content = text,
            Padding = new global::Avalonia.Thickness(0, 6),
            MinHeight = 32,
            HorizontalAlignment = HorizontalAlignment.Left,
            FontSize = 13,
            Foreground = CardBrush("EchoAccentTextBrush", "#5EF0C1")
        };
        link.Classes.Add("ghost");
        link.Click += (_, _) => click();
        return link;
    }

    /// <summary>
    /// A step of the setup with its number in a circle (<paramref name="number"/> 0: an optional one, no number).
    /// Off: «Включить», bright only on the <paramref name="next"/> step; on: a tick and a quiet «Изменить»
    /// (<paramref name="change"/>) that opens the Android page where it can be turned off again.
    /// </summary>
    private void AddSetupRow(int number, string title, string? hint, bool done, bool next, Action turnOn, string? buttonText = null, Action? change = null, Control? below = null)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        if (number > 0 || done)
        {
            var mark = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new global::Avalonia.CornerRadius(14),
                Margin = new global::Avalonia.Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Top,
                BorderThickness = new global::Avalonia.Thickness(done ? 0 : 1.5),
                BorderBrush = next ? CardBrush("EchoAccentBrush", "#5EF0C1") : CardBrush("EchoBorderStrongBrush", "#3A4350"),
                Background = done ? CardBrush("EchoAccentBrush", "#5EF0C1") : null,
                Child = new TextBlock
                {
                    Text = done ? "✓" : number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    FontSize = 14,
                    FontWeight = FontWeight.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = done ? CardBrush("EchoAccentInkBrush", "#0E1116") : next ? CardBrush("EchoAccentTextBrush", "#5EF0C1") : CardBrush("EchoMutedBrush", "#9AA5B5")
                }
            };
            row.Children.Add(mark);
        }

        var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = done ? CardBrush("EchoSoftTextBrush", "#C9D2DE") : CardBrush("EchoTextBrush", "#E9EEF5")
        });
        if (!string.IsNullOrEmpty(hint))
        {
            texts.Children.Add(new TextBlock
            {
                Text = hint,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = CardBrush("EchoMutedBrush", "#9AA5B5")
            });
        }

        if (below != null)
        {
            texts.Children.Add(below);
        }

        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);
        if (!done)
        {
            var button = new Button
            {
                Content = buttonText ?? CallText.Get("SetupTurnOn"),
                MinHeight = 44,
                Padding = new global::Avalonia.Thickness(16, 0),
                Margin = new global::Avalonia.Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            if (next)
            {
                button.Classes.Add("accent");
            }

            button.Click += (_, _) => turnOn();
            Grid.SetColumn(button, 2);
            row.Children.Add(button);
        }
        else if (change != null)
        {
            var button = new Button
            {
                Content = CallText.Get("SetupChange"),
                MinHeight = 44,
                Padding = new global::Avalonia.Thickness(14, 0),
                Margin = new global::Avalonia.Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            button.Classes.Add("ghost");
            button.Click += (_, _) => change();
            Grid.SetColumn(button, 2);
            row.Children.Add(button);
        }

        CallSetupList.Children.Add(row);
    }

    private static void OpenAppInfo(Context context)
    {
        try
        {
            var intent = new Intent(Settings.ActionApplicationDetailsSettings, global::Android.Net.Uri.Parse("package:" + context.PackageName));
            intent.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            PhonePlatform.Log("[calls] App info could not be opened: " + ex.Message);
        }
    }

    /// <summary>The list of apps Android may put to sleep (to let it put Replio to sleep again).</summary>
    private static void OpenBatterySettings(Context context)
    {
        try
        {
            var intent = new Intent(Settings.ActionIgnoreBatteryOptimizationSettings);
            intent.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            PhonePlatform.Log("[calls] Battery list could not be opened: " + ex.Message);
            OpenAppInfo(context);
        }
    }

    private static bool IsIgnoringBatteryOptimizations(Context context) =>
        Build.VERSION.SdkInt < BuildVersionCodes.M ||
        (context.GetSystemService(Context.PowerService) as PowerManager)?.IsIgnoringBatteryOptimizations(context.PackageName) == true;

    private static void RequestIgnoreBatteryOptimizations(Context context)
    {
        try
        {
            var intent = new Intent(Settings.ActionRequestIgnoreBatteryOptimizations, global::Android.Net.Uri.Parse("package:" + context.PackageName));
            intent.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            PhonePlatform.Log("[calls] Battery setting could not be opened: " + ex.Message);
        }
    }
}
