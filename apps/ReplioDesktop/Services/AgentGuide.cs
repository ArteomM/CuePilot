using System.IO;
using System.Text;

namespace ReplioDesktop.Services;

/// <summary>
/// How a local AI agent (Claude Code, Codex, Cursor…) finds Replio sessions: the app keeps
/// %AppData%\Replio\AGENTS.md up to date with the path of the «replio» command and the data layout,
/// and «replio guide» prints the same text. The file stays in the per-device folder even when the data folder
/// is in a cloud drive: its paths (the command, the user name) differ from computer to computer.
/// </summary>
public static class AgentGuide
{
    public const string FileName = "AGENTS.md";
    private const string Heading = "# Replio: sessions for AI agents";

    /// <summary>The data folder (REPLIO_DATA, the folder chosen in Settings, or %AppData%\Replio).</summary>
    public static string DataRoot => DataLocation.Root;

    /// <summary>%AppData%\Replio\AGENTS.md: per device, never in the synced data folder.</summary>
    public static string GuidePath => Path.Combine(DataLocation.ConfigRoot, FileName);

    /// <summary>replio.exe on Windows; replio-cli inside Replio.app (the Mac disk ignores case: «replio» = «Replio»).</summary>
    public static string CliFileName => OperatingSystem.IsWindows() ? "replio.exe" : "replio-cli";

    /// <summary>
    /// True when macOS runs Replio from a temporary copy (App Translocation: started from the .dmg or Downloads without
    /// moving it to Applications) or straight from the mounted .dmg: the command path would break after a restart.
    /// </summary>
    public static bool IsTemporaryMacLocation =>
        OperatingSystem.IsMacOS() &&
        (AppContext.BaseDirectory.Contains("/AppTranslocation/", StringComparison.Ordinal) ||
         AppContext.BaseDirectory.StartsWith("/Volumes/", StringComparison.Ordinal));

    /// <summary>The command next to the running app, or empty when this build has none.</summary>
    public static string FindCli()
    {
        string path = Path.Combine(AppContext.BaseDirectory, CliFileName);
        return File.Exists(path) ? path : string.Empty;
    }

    public static string Text(string cliPath)
    {
        string cli = string.IsNullOrWhiteSpace(cliPath) ? CliFileName : cliPath;
        string quoted = cli.Contains(' ') ? "\"" + cli + "\"" : cli;
        string root = DataRoot;
        var text = new StringBuilder();
        text.AppendLine(Heading)
            .AppendLine()
            .AppendLine("Replio records meetings (transcript, AI summary, screenshots) on this computer. " +
                        "Use the `replio` command to find a session by name or time and read or export it. " +
                        "It only reads Replio data; nothing is changed or sent anywhere.")
            .AppendLine()
            .AppendLine("Command: `" + cli + "`" + (string.IsNullOrWhiteSpace(cliPath) ? " (not found next to the app; install Replio 1.1.0 or newer)" : string.Empty))
            .AppendLine()
            .AppendLine("## Commands")
            .AppendLine()
            .AppendLine("```")
            .AppendLine(quoted + " list [--query TEXT] [--topic NAME] [--from DATE] [--to DATE] [--limit N] [--json]")
            .AppendLine(quoted + " show SESSION [--no-transcript] [--json]      # summary, transcript, screenshot paths as Markdown")
            .AppendLine(quoted + " export SESSION [--out DIR] [--json]          # folder with session.md, summary.md, transcript.md, screenshots/, session.json")
            .AppendLine(quoted + " topics [--json]")
            .AppendLine(quoted + " topic NAME [--json]                          # topic overview, its sessions and knowledge files")
            .AppendLine(quoted + " where [--json]                               # data folders")
            .AppendLine(quoted + " mcp                                          # MCP server over stdio")
            .AppendLine("```")
            .AppendLine()
            .AppendLine("SESSION is any of: `last` (newest), `last-2` (the one before), a session id (or its first 6+ characters), " +
                        "a time such as `2026-11-17 14:05`, `17.11.2026 14:05`, `14:05` (today) or `2026-11-17` (that day), " +
                        "or words of the title, e.g. `\"KEGOC разбор DEMO5\"`. Add `--topic NAME` to look only inside a topic. " +
                        "When several sessions match, the command lists them and exits with code 2: pick one by id.")
            .AppendLine()
            .AppendLine("`--json` gives machine-readable output. Exit codes: 0 ok, 1 error, 2 more than one match, 3 nothing found.")
            .AppendLine()
            .AppendLine("## MCP")
            .AppendLine()
            .AppendLine("`" + quoted + " mcp` is an MCP server (stdio) with tools list_sessions, get_session, export_session, " +
                        "get_screenshot, list_topics, get_topic. For Claude Code: `claude mcp add replio -- " + quoted + " mcp`.")
            .AppendLine()
            .AppendLine("## Data layout (read-only for agents)")
            .AppendLine()
            .AppendLine("- Sessions: `" + Path.Combine(root, "sessions") + "` — one `<id>.json` per session " +
                        "(Title, StartedAt, EndedAt, TopicId, SummaryText in Markdown, Transcript[], Screenshots[]).")
            .AppendLine("- Screenshots: `" + Path.Combine(root, "visual-context", "<sessionId>", "screenshots") + "` (JPEG).")
            .AppendLine("- Topics: `" + Path.Combine(root, "topics.json") + "` (Name, KnowledgeText = topic overview, Sources[]).")
            .AppendLine("- Parsed topic documents: `" + Path.Combine(root, "knowledge", "<topicId>", "<sourceId>.md") + "`.")
            .AppendLine("- Exports: `" + Path.Combine(root, "exports") + "` unless `--out` is given.")
            .AppendLine()
            .AppendLine("A session whose recording is still running has Status 0 (Active) and grows while you read it.");
        return text.ToString();
    }

    /// <summary>Writes AGENTS.md into the per-device folder when its text changed. Never throws.</summary>
    public static void WriteIfChanged(string cliPath)
    {
        RemoveFromSyncedFolder();
        try
        {
            string path = GuidePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string text = Text(cliPath);
            if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) == text)
            {
                return;
            }

            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        catch (Exception)
        {
            // Only a convenience for agents: the app works without it.
        }
    }

    /// <summary>
    /// 1.1.1 wrote AGENTS.md into the data folder. In a cloud folder two computers would keep overwriting it with
    /// their own paths, so a copy written by Replio is removed there. Never throws.
    /// </summary>
    private static void RemoveFromSyncedFolder()
    {
        try
        {
            string old = Path.Combine(DataRoot, FileName);
            if (string.Equals(Path.GetFullPath(old), Path.GetFullPath(GuidePath), StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(old))
            {
                return;
            }

            using (var reader = new StreamReader(old, Encoding.UTF8))
            {
                if (reader.ReadLine()?.Trim() != Heading)
                {
                    return;
                }
            }

            File.Delete(old);
        }
        catch (Exception)
        {
            // Left in place: agents read the copy in the per-device folder anyway.
        }
    }
}
