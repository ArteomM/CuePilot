using System.Diagnostics;
using System.IO;
using ReplioDesktop.Services;

namespace ReplioDesktop.ViewModels;

/// <summary>
/// «Справка»: a page with a short guide (HelpBody in the resx files, Markdown). Placeholders are filled from this
/// installation: the user's own hotkeys, the path of the «replio» command and the MCP lines for agents.
/// </summary>
public partial class MainViewModel
{
    private string _helpFeedback = string.Empty;

    public RelayCommand OpenHelpCommand { get; private set; } = null!;
    public RelayCommand CopyMcpCommandCommand { get; private set; } = null!;
    public RelayCommand OpenAgentsGuideCommand { get; private set; } = null!;

    public string HelpFeedback => _helpFeedback;

    public string HelpText
    {
        get
        {
            string cli = CliCommandForAgents();
            string jsonPath = cli.Replace("\\", "\\\\").Replace("\"", "\\\"");
            return L["HelpBody"]
                .Replace("{START}", Key(ToggleCaptureHotkeyText))
                .Replace("{OVERLAY}", Key(ToggleOverlayHotkeyText))
                .Replace("{ASK}", Key(AskHotkeyText))
                .Replace("{SNAPSHOT}", Key(SnapshotHotkeyText))
                .Replace("{HOTKEYS}", HotkeyTable())
                .Replace("{MCP_CLAUDE}", McpClaudeCommand())
                .Replace("{MCP_JSON}", "{\n  \"mcpServers\": {\n    \"replio\": { \"command\": \"" + jsonPath + "\", \"args\": [\"mcp\"] }\n  }\n}")
                .Replace("{AGENTS}", Path.Combine(AgentGuide.DataRoot, AgentGuide.FileName));
        }
    }

    private void InitializeHelp()
    {
        OpenHelpCommand = new RelayCommand(() =>
        {
            SetHelpFeedback(string.Empty);
            OnPropertyChanged(nameof(HelpText));
            SelectedMainPage = "Help";
        });
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
            string path = Path.Combine(AgentGuide.DataRoot, AgentGuide.FileName);
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex)
            {
                SetHelpFeedback(ex.Message);
            }
        });
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

    private static string McpClaudeCommand()
    {
        string cli = CliCommandForAgents();
        return "claude mcp add replio -- " + (cli.Contains(' ') ? "\"" + cli + "\"" : cli) + " mcp";
    }

    private void SetHelpFeedback(string text)
    {
        _helpFeedback = text;
        OnPropertyChanged(nameof(HelpFeedback));
    }
}
