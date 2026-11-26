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
/// The card at the top of Home: «Replio ждёт звонка» when everything is set, otherwise the steps that are still off
/// (microphone, «Слушать звонок», not putting Replio to sleep), each with its own button.
/// </summary>
public partial class MainView
{
    private void SetUpCallCard()
    {
        CallAssistant.Instance.StateChanged += () => Dispatcher.UIThread.Post(UpdateCallCard);
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

    private void FillCallCard()
    {
        Context context = global::Android.App.Application.Context;
        bool mic = PhonePlatform.HasMicrophonePermission();
        bool listen = ReplioAccessibilityService.IsEnabled(context);
        bool battery = IsIgnoringBatteryOptimizations(context);
        CallAssistant assistant = CallAssistant.Instance;

        CallSetupList.Children.Clear();
        if (assistant.IsRecording)
        {
            CallStatusTitle.Text = CallText.Format("RecordingTitle", assistant.ElapsedText);
            CallStatusText.Text = assistant.HearingText;
            CallStatusDot.Fill = new SolidColorBrush(Color.Parse("#E5484D"));
        }
        else if (assistant.InCall)
        {
            CallStatusTitle.Text = CallText.Get("InCallTitle");
            CallStatusText.Text = CallText.Get("InCallText");
            CallStatusDot.Fill = CardBrush("EchoAccentBrush", "#5EF0C1");
        }
        else if (mic && listen)
        {
            CallStatusTitle.Text = CallText.Get("WaitingTitle");
            CallStatusText.Text = CallText.Get("SetupReady");
            CallStatusDot.Fill = CardBrush("EchoAccentBrush", "#5EF0C1");
        }
        else
        {
            CallStatusTitle.Text = CallText.Get("SetupTitle");
            CallStatusText.Text = CallText.Get("SetupNeeded");
            CallStatusDot.Fill = CardBrush("EchoMutedBrush", "#9AA5B5");
        }

        AddSetupRow(CallText.Get("SetupMic"), null, mic, async () =>
        {
            await PhonePlatform.RequestMicrophonePermissionAsync();
            UpdateCallCard();
        });
        AddSetupRow(CallText.Get("SetupListen"), listen ? null : CallText.Get("SetupListenHint"), listen,
            () => ReplioAccessibilityService.OpenSettings(MainActivity.Current ?? context));
        if (!listen)
        {
            AddSetupRow(CallText.Get("SetupRestricted"), null, false, () => OpenAppInfo(MainActivity.Current ?? context), CallText.Get("SetupAppInfo"));
        }

        AddSetupRow(CallText.Get("SetupBattery"), null, battery, () => RequestIgnoreBatteryOptimizations(MainActivity.Current ?? context));
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
        });
        CallSpeakerNote.Text = CallText.Get("SetupSpeaker");

        // The card stays two lines high; the steps open in a sheet that scrolls.
        int left = (mic ? 0 : 1) + (listen ? 0 : 1) + (battery ? 0 : 1);
        CallSetupTitle.Text = CallText.Get("SetupTitle");
        CallSetupButton.Content = left > 0 ? CallText.Format("SetupOpenLeft", left) : CallText.Get("SetupOpen");
        CallSetupButton.Classes.Set("accent", left > 0);
        if (left == 0 && !assistant.IsRecording && !assistant.InCall)
        {
            CallStatusText.Text = CallText.Get("SetupReadyShort");
        }
        else if (left > 0 && !assistant.IsRecording && !assistant.InCall)
        {
            CallStatusText.Text = CallText.Get("SetupNeededShort");
        }
    }

    private void CallSetup_OnClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        UpdateCallCard();
        CallSetupPanel.MaxHeight = Math.Max(320, Bounds.Height * 0.85);
        CallSetupSheet.IsVisible = true;
    }

    private void CloseCallSetup_OnClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e) => CallSetupSheet.IsVisible = false;

    private void CallSetupSheetBackdrop_OnPointerPressed(object? sender, global::Avalonia.Input.PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, CallSetupSheet))
        {
            CallSetupSheet.IsVisible = false;
        }
    }

    private void AddSetupRow(string title, string? hint, bool done, Action turnOn, string? buttonText = null)
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
