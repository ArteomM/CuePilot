using System.Diagnostics;
using System.IO;
using ReplioDesktop.Services;

namespace ReplioDesktop.ViewModels;

/// <summary>One page of the help window: a title, a few short lines (Markdown) and a small screenshot.</summary>
public sealed class HelpTopic
{
    public HelpTopic(string key, string title, string markdown, string image)
    {
        Key = key;
        Title = title;
        Markdown = markdown;
        Image = image;
    }

    public string Key { get; }
    public string Title { get; }
    public string Markdown { get; }

    /// <summary>File name in Assets/Help without the theme suffix and extension («overlay» → overlay-dark.png); empty for none.</summary>
    public string Image { get; }

    public bool IsAgents => Key == "Agents";

    /// <summary>The title: what screen readers and type-to-search in the page list see.</summary>
    public override string ToString() => Title;
}

/// <summary>
/// «Справка»: a small window with short pages (HelpTopic*Title/Body in the resx files, Markdown) and mini screenshots.
/// Placeholders are filled from this installation: the user's own hotkeys, the path of the «replio» command and the MCP
/// lines for agents, so every user and computer sees its own paths.
/// </summary>
public partial class MainViewModel
{
    // Page keys in order; each has HelpTopic{Key}Title and HelpTopic{Key}Body strings.
    private static readonly (string Key, string Image)[] HelpPages =
    [
        ("Start", "settings-account"),
        ("Record", "home"),
        ("Hints", "overlay"),
        ("After", "session"),
        ("Topics", "topic"),
        ("Meetings", "topic-meetings"),
        ("Search", "search"),
        ("Hotkeys", string.Empty),
        ("Data", "settings-data"),
        ("Agents", string.Empty),
        ("Trouble", string.Empty)
    ];

    private string _helpFeedback = string.Empty;
    private IReadOnlyList<HelpTopic> _helpTopics = [];
    private HelpTopic? _selectedHelpTopic;

    public RelayCommand OpenHelpCommand { get; private set; } = null!;
    public RelayCommand CopyMcpCommandCommand { get; private set; } = null!;
    public RelayCommand OpenAgentsGuideCommand { get; private set; } = null!;
    public RelayCommand NextHelpTopicCommand { get; private set; } = null!;
    public RelayCommand PreviousHelpTopicCommand { get; private set; } = null!;

    /// <summary>The view opens (or brings forward) the help window.</summary>
    public event Action? HelpWindowRequested;

    public string HelpFeedback => _helpFeedback;

    public IReadOnlyList<HelpTopic> HelpTopics => _helpTopics;

    public HelpTopic? SelectedHelpTopic
    {
        get => _selectedHelpTopic;
        set
        {
            if (ReferenceEquals(_selectedHelpTopic, value))
            {
                return;
            }

            _selectedHelpTopic = value;
            SetHelpFeedback(string.Empty);
            OnPropertyChanged();
            OnPropertyChanged(nameof(HelpCounterText));
            OnPropertyChanged(nameof(IsAgentsHelpTopic));
            NextHelpTopicCommand.RaiseCanExecuteChanged();
            PreviousHelpTopicCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsAgentsHelpTopic => _selectedHelpTopic?.IsAgents == true;

    public string HelpCounterText
    {
        get
        {
            int index = SelectedHelpIndex;
            return index < 0 ? string.Empty : string.Format(L["HelpCounter"], index + 1, _helpTopics.Count);
        }
    }

    private int SelectedHelpIndex => _selectedHelpTopic == null ? -1 : IndexOfHelpTopic(_selectedHelpTopic.Key);

    private void InitializeHelp()
    {
        OpenHelpCommand = new RelayCommand(() =>
        {
            RefreshHelpTopics();
            HelpWindowRequested?.Invoke();
        });
        NextHelpTopicCommand = new RelayCommand(
            () => SelectedHelpTopic = _helpTopics[SelectedHelpIndex + 1],
            () => SelectedHelpIndex >= 0 && SelectedHelpIndex < _helpTopics.Count - 1);
        PreviousHelpTopicCommand = new RelayCommand(
            () => SelectedHelpTopic = _helpTopics[SelectedHelpIndex - 1],
            () => SelectedHelpIndex > 0);
        CopyMcpCommandCommand = new RelayCommand(() =>
        {
            try
            {
                SetClipboardText(McpClaudeCommand());
                SetHelpFeedback(L["HelpCopied"]);
            }
            catch (Exception ex)
            {
                SetHelpFeedback(ex.Message);
            }
        });
        OpenAgentsGuideCommand = new RelayCommand(() =>
        {
            AgentGuide.WriteIfChanged(AgentGuide.FindCli());
            try
            {
                Process.Start(new ProcessStartInfo(AgentGuide.GuidePath) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex)
            {
                SetHelpFeedback(ex.Message);
            }
        });
    }

    /// <summary>Rebuilds the pages (language, hotkeys or paths may have changed) and keeps the open page.</summary>
    public void RefreshHelpTopics()
    {
        string selected = _selectedHelpTopic?.Key ?? HelpPages[0].Key;
        _helpTopics = HelpPages
            .Select(page => new HelpTopic(page.Key, L["HelpTopic" + page.Key + "Title"], FillHelp(L["HelpTopic" + page.Key + "Body"]), page.Image))
            .ToList();
        OnPropertyChanged(nameof(HelpTopics));
        _selectedHelpTopic = null;
        SelectedHelpTopic = _helpTopics[Math.Max(0, IndexOfHelpTopic(selected))];
    }

    private int IndexOfHelpTopic(string key)
    {
        for (int i = 0; i < _helpTopics.Count; i++)
        {
            if (_helpTopics[i].Key == key)
            {
                return i;
            }
        }

        return -1;
    }

    private string FillHelp(string body)
    {
        string cli = CliCommandForAgents();
        string jsonPath = cli.Replace("\\", "\\\\").Replace("\"", "\\\"");
        string shortCli = OperatingSystem.IsWindows() ? "replio" : Quote(cli);
        return body
            .Replace("{START}", Key(ToggleCaptureHotkeyText))
            .Replace("{OVERLAY}", Key(ToggleOverlayHotkeyText))
            .Replace("{ASK}", Key(AskHotkeyText))
            .Replace("{SNAPSHOT}", Key(SnapshotHotkeyText))
            .Replace("{PASTE}", OperatingSystem.IsMacOS() ? "**⌘V**" : "**Ctrl+V**")
            .Replace("{HOTKEYS}", HotkeyTable())
            .Replace("{MCP_CLAUDE}", McpClaudeCommand())
            .Replace("{MCP_JSON}", "{\n  \"mcpServers\": {\n    \"replio\": { \"command\": \"" + jsonPath + "\", \"args\": [\"mcp\"] }\n  }\n}")
            .Replace("{CLI}", shortCli)
            .Replace("{MAC_WARNING}", AgentGuide.IsTemporaryMacLocation ? "> " + L["HelpMacMoveWarning"] + "\n\n" : string.Empty)
            .Replace("{AGENTS}", AgentGuide.GuidePath);
    }

    private static string Key(string hotkey) => "**" + (string.IsNullOrWhiteSpace(hotkey) ? "—" : hotkey.Trim()) + "**";

    private string HotkeyTable()
    {
        var rows = new (string Label, string Keys)[]
        {
            (L["HotkeyStartStop"], ToggleCaptureHotkeyText),
            (L["HotkeyShowHide"], ToggleOverlayHotkeyText),
            (L["HotkeyAsk"], AskHotkeyText),
            (L["HotkeySnapshot"], SnapshotHotkeyText),
            (L["HotkeyClearChat"], ClearChatHotkeyText),
            (L["HotkeyMove"], string.Join(" ", new[] { MoveLeftHotkeyText, MoveRightHotkeyText, MoveUpHotkeyText, MoveDownHotkeyText }.Where(k => !string.IsNullOrWhiteSpace(k)))),
            (L["HotkeyScroll"], string.Join(" ", new[] { ScrollUpHotkeyText, ScrollDownHotkeyText }.Where(k => !string.IsNullOrWhiteSpace(k))))
        };
        return string.Join("\n", rows.Select(r => "- " + r.Label + ": " + Key(r.Keys)));
    }

    /// <summary>The installed «replio» command; just «replio» (found through PATH) when this build has none next to it.</summary>
    private static string CliCommandForAgents()
    {
        string path = AgentGuide.FindCli();
        return string.IsNullOrWhiteSpace(path) ? "replio" : path;
    }

    private static string Quote(string path) => path.Contains(' ') ? "\"" + path + "\"" : path;

    private static string McpClaudeCommand() => "claude mcp add replio -- " + Quote(CliCommandForAgents()) + " mcp";

    private void SetHelpFeedback(string text)
    {
        _helpFeedback = text;
        OnPropertyChanged(nameof(HelpFeedback));
    }
}
