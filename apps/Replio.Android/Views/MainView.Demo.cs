using Avalonia.Threading;
using Replio.Android.Platform;
using ReplioDesktop.Models;
using ReplioDesktop.ViewModels;

namespace Replio.Android.Views;

/// <summary>
/// «[ui] ready» in the log once the screen is drawn, and, in the emulator build of CI only, pages opened by an
/// intent extra (adb shell am start … --es replio_demo live) so that every screen can be photographed.
/// </summary>
public partial class MainView
{
    private static string? _pendingDemo;
    private bool _readyLogged;
    private bool _demoLive;

    /// <summary>The screen of the app (one per process).</summary>
    public static MainView? Current { get; private set; }

    private void SetUpReadyLog()
    {
        Current = this;
        LayoutUpdated += (_, _) =>
        {
            if (_readyLogged || Bounds.Width <= 0)
            {
                return;
            }

            _readyLogged = true;
            Dispatcher.UIThread.Post(() =>
            {
                PhonePlatform.Log($"[ui] ready {Bounds.Width:0}x{Bounds.Height:0}");
                if (_pendingDemo is { } page)
                {
                    _pendingDemo = null;
                    ShowDemo(page);
                }
            }, DispatcherPriority.Background);
        };
    }

    /// <summary>Opens a page for a screenshot; before the screen exists the page waits for it.</summary>
    public static void RequestDemo(string page)
    {
        if (Current is { _readyLogged: true } view)
        {
            Dispatcher.UIThread.Post(() => view.ShowDemo(page));
        }
        else
        {
            _pendingDemo = page;
        }
    }

    private void ShowDemo(string page)
    {
        try
        {
            PhonePlatform.Log("[demo] page " + page);
#if REPLIO_DEMO
            Replio.Android.Platform.Calls.CallAssistant.Instance.DemoOverlay(page);
#endif
            CloseDialog();
            CallSetupSheet.IsVisible = false;
            KnowledgeSheet.IsVisible = false;
            _demoLive = false;
            switch (page)
            {
                case "settings":
                    Execute(_viewModel.OpenSettingsCommand);
                    break;
                case "about":
                    Execute(_viewModel.OpenSettingsCommand);
                    _viewModel.IsSettingsData = true;
                    break;
                case "setup":
                    Execute(_viewModel.GoHomeCommand);
                    CallSetup_OnClick(null, new global::Avalonia.Interactivity.RoutedEventArgs());
                    break;
                case "group":
                    Execute(_viewModel.GoHomeCommand);
                    _viewModel.SelectedTopicItem = _viewModel.TopicItems.FirstOrDefault(i => i.IsTopic) ?? _viewModel.SelectedTopicItem;
                    break;
                case "session":
                    OpenDemoSession();
                    _assistTab = false;
                    _viewModel.SelectedSessionTab = "Summary";
                    UpdateSessionPanels();
                    break;
                case "live":
                    OpenDemoSession();
                    _demoLive = true;
                    _assistTab = true;
                    DemoChat();
                    UpdateSessionPanels();
                    break;
                case "livetext":
                    OpenDemoSession();
                    _demoLive = true;
                    _assistTab = false;
                    _viewModel.SelectedSessionTab = "Transcript";
                    UpdateSessionPanels();
                    break;
                case "callmenu":
                case "callhint":
                    Execute(_viewModel.GoHomeCommand);
                    break;
                case "actions":
                    Execute(_viewModel.GoHomeCommand);
                    _viewModel.SelectedTopicItem = _viewModel.TopicItems.FirstOrDefault();
                    if (_viewModel.CompletedSessions.FirstOrDefault() is { } first)
                    {
                        ShowSessionActions(first);
                    }

                    break;
                default:
                    Execute(_viewModel.GoHomeCommand);
                    _viewModel.SelectedTopicItem = _viewModel.TopicItems.FirstOrDefault();
                    break;
            }
        }
        catch (Exception ex)
        {
            PhonePlatform.Log("[demo] " + page + " failed: " + ex);
        }
    }

    private void OpenDemoSession()
    {
        Execute(_viewModel.GoHomeCommand);
        _viewModel.SelectedTopicItem = _viewModel.TopicItems.FirstOrDefault();
        if (_viewModel.CompletedSessions.FirstOrDefault() is SessionSummary session)
        {
            Execute(_viewModel.OpenSessionCommand, session);
        }
    }

    private void DemoChat()
    {
        _chat.Clear();
        AppendChat(isUser: true, MobileText.Get("ActWhatToSay"));
        AppendChat(isUser: false, "Предложите зафиксировать срок 15 декабря и попросите прислать договор до пятницы: тогда отгрузка успеет до праздников.");
        AppendChat(isUser: true, "Какая скидка возможна?");
    }
}
