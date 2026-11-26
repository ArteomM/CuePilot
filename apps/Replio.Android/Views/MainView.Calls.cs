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

    private void SetUpCallCard()
    {
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
        bool mic = PhonePlatform.HasMicrophonePermission();
        bool listen = ReplioAccessibilityService.IsEnabled(context);
        bool battery = IsIgnoringBatteryOptimizations(context);
        CallAssistant assistant = CallAssistant.Instance;

        CallWatchSwitch.IsChecked = assistant.WatchCalls;
        CallWatchTitle.Text = CallText.Get("SetupWatch");
        CallWatchText.Text = CallText.Get(assistant.WatchCalls ? "SetupWatchOn" : "SetupWatchOff");
        CallSetupList.Opacity = assistant.WatchCalls ? 1 : 0.5;

        CallSetupList.Children.Clear();
        AddSetupRow(CallText.Get("SetupMic"), null, mic, async () =>
        {
            await PhonePlatform.RequestMicrophonePermissionAsync();
            UpdateCallCard();
        }, change: () => OpenAppInfo(MainActivity.Current ?? context));
        AddSetupRow(CallText.Get("SetupListen"), listen ? null : CallText.Get("SetupListenHint"), listen,
            () => ReplioAccessibilityService.OpenSettings(MainActivity.Current ?? context),
            change: () => ReplioAccessibilityService.OpenSettings(MainActivity.Current ?? context));
        if (!listen)
        {
            AddSetupRow(CallText.Get("SetupRestricted"), CallText.Get("SetupRestrictedHint"), false, () => OpenAppInfo(MainActivity.Current ?? context), CallText.Get("SetupAppInfo"));
        }

        AddSetupRow(CallText.Get("SetupBattery"), null, battery, () => RequestIgnoreBatteryOptimizations(MainActivity.Current ?? context),
            change: () => OpenBatterySettings(MainActivity.Current ?? context));
        bool recordings = CallRecordingImporter.HasPermission(context);
        AddSetupRow(CallText.Get("SetupRecordings"), recordings ? null : CallText.Get("SetupRecordingsHint"), recordings, async () =>
        {
            if (MainActivity.Current is { } activity)
            {
                await activity.RequestPermissionAsync(CallRecordingImporter.Permission);
                // Recordings from now on are picked up, older ones are left alone.
                CallRecordingImporter.MarkNow();
            }

            UpdateCallCard();
        }, change: () => OpenAppInfo(MainActivity.Current ?? context));

        int left = (mic ? 0 : 1) + (listen ? 0 : 1) + (battery ? 0 : 1);
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
    /// A step of the setup: off, a mint «Включить»; on, a tick and a quiet «Изменить» (<paramref name="change"/>)
    /// that opens the Android page where it can be turned off again.
    /// </summary>
    private void AddSetupRow(string title, string? hint, bool done, Action turnOn, string? buttonText = null, Action? change = null)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock
        {
            Text = (done ? "✓  " : string.Empty) + title,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = done ? CardBrush("EchoAccentBrush", "#5EF0C1") : CardBrush("EchoTextBrush", "#E9EEF5")
        });
        if (!string.IsNullOrEmpty(hint))
        {
            texts.Children.Add(new TextBlock
            {
                Text = hint,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = CardBrush("EchoMutedBrush", "#9AA5B5")
            });
        }

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
            button.Classes.Add("accent");
            button.Click += (_, _) => turnOn();
            Grid.SetColumn(button, 1);
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
            Grid.SetColumn(button, 1);
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
