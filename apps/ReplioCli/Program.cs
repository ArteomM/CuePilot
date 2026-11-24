using System.Text;
using System.Text.Json.Nodes;
using ReplioCli;
using ReplioDesktop.Models;
using ReplioDesktop.Services;

// replio: Replio sessions for local AI agents. Exit codes: 0 ok, 1 error, 2 several matches, 3 nothing found.
var utf8 = new UTF8Encoding(false);
Console.OutputEncoding = utf8;

var options = Options.Parse(args);
if (options.Command is "" or "help" or "-h" or "--help" or "/?")
{
    Console.WriteLine(Help.Text);
    return 0;
}

if (options.Command is "version" or "--version" or "-v")
{
    Console.WriteLine(Help.Version);
    return 0;
}

try
{
    string root = options.Value("data") ?? AgentGuide.DataRoot;
    switch (options.Command)
    {
        case "mcp":
            return await McpServer.RunAsync(root);
        case "guide":
            Console.Write(AgentGuide.Text(Environment.ProcessPath ?? string.Empty));
            return 0;
        case "where":
            return Where(new Archive(root), options);
        case "list":
        case "ls":
        case "sessions":
            return List(new Archive(root), options);
        case "show":
        case "get":
            return Show(new Archive(root), options);
        case "export":
            return Export(new Archive(root), options);
        case "topics":
            return Topics(new Archive(root), options);
        case "topic":
            return Topic(new Archive(root), options);
        default:
            Console.Error.WriteLine($"Unknown command «{options.Command}». Run: replio help");
            return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 1;
}

static int Where(Archive archive, Options options)
{
    var json = new JsonObject
    {
        ["data"] = archive.Root,
        ["sessions"] = archive.SessionsFolder,
        ["screenshots"] = archive.ScreenshotsRoot,
        ["topics"] = Path.Combine(archive.Root, "topics.json"),
        ["knowledge"] = archive.KnowledgeRoot,
        ["exports"] = archive.ExportsFolder,
        ["guide"] = Path.Combine(archive.Root, AgentGuide.FileName),
        ["cli"] = Environment.ProcessPath,
        ["sessionCount"] = archive.Sessions.Count,
        ["topicCount"] = archive.Topics.Count
    };
    if (options.Json)
    {
        Console.WriteLine(json.ToJsonString(Render.JsonOutput));
        return 0;
    }

    foreach (KeyValuePair<string, JsonNode?> pair in json)
    {
        Console.WriteLine($"{pair.Key,-13} {pair.Value}");
    }

    return 0;
}

static int List(Archive archive, Options options)
{
    SessionTopic? topic = null;
    if (options.Value("topic") is { } topicWords && !Cli.TryTopic(archive, topicWords, options, out topic))
    {
        return Cli.LastExit;
    }

    DateTime? from = Bound(options.Value("from"), end: false);
    DateTime? to = Bound(options.Value("to"), end: true);
    if (options.Value("date") is { } day && Archive.TryParseBound(day, false, out DateTime dayStart))
    {
        from = dayStart.Date;
        to = dayStart.Date.AddDays(1).AddTicks(-1);
    }

    string? query = options.Value("query") ?? (options.Positional.Count > 0 ? string.Join(' ', options.Positional) : null);
    int limit = int.TryParse(options.Value("limit"), out int n) && n > 0 ? n : 50;
    List<SessionSummary> sessions = archive.Filter(query, topic, from, to).Take(limit).ToList();
    Console.WriteLine(options.Json ? Render.ListJson(archive, sessions) : Render.ListText(archive, sessions));
    return sessions.Count == 0 ? 3 : 0;
}

static int Show(Archive archive, Options options)
{
    if (!Cli.TrySelect(archive, options, out SessionSummary? session))
    {
        return Cli.LastExit;
    }

    bool transcript = !options.Flag("no-transcript");
    if (options.Json)
    {
        Console.WriteLine(Render.SessionJson(archive, session!, transcript).ToJsonString(Render.JsonOutput));
        return 0;
    }

    Console.Write(Render.SessionMarkdown(archive, session!, transcript, path => new Uri(Path.GetFullPath(path)).AbsoluteUri));
    return 0;
}

static int Export(Archive archive, Options options)
{
    if (!Cli.TrySelect(archive, options, out SessionSummary? session))
    {
        return Cli.LastExit;
    }

    Exporter.Result result = Exporter.Export(archive, session!, options.Value("out"));
    if (options.Json)
    {
        JsonObject json = Render.SessionHeader(archive, session!);
        json["exportFolder"] = result.Folder;
        json["exportFiles"] = new JsonArray(result.Files.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray());
        Console.WriteLine(json.ToJsonString(Render.JsonOutput));
        return 0;
    }

    Console.WriteLine(result.Folder);
    Console.Error.WriteLine($"Exported «{session!.Title}»: session.md, summary.md, transcript.md, session.json" +
                            (session.Screenshots.Count > 0 ? $", {session.Screenshots.Count} screenshot(s)" : string.Empty) + ".");
    return 0;
}

static int Topics(Archive archive, Options options)
{
    Console.WriteLine(options.Json ? Render.TopicsJson(archive) : Render.TopicsText(archive));
    return archive.Topics.Count == 0 ? 3 : 0;
}

static int Topic(Archive archive, Options options)
{
    string words = string.Join(' ', options.Positional);
    if (!Cli.TryTopic(archive, words, options, out SessionTopic? topic))
    {
        return Cli.LastExit;
    }

    Console.WriteLine(options.Json ? Render.TopicJson(archive, topic!).ToJsonString(Render.JsonOutput) : Render.TopicMarkdown(archive, topic!));
    return 0;
}

static DateTime? Bound(string? text, bool end) =>
    text != null && Archive.TryParseBound(text, end, out DateTime value) ? value : null;

/// <summary>Picking a session or topic from the user's words, with the candidates printed when it is not one.</summary>
internal static class Cli
{
    public static int LastExit { get; private set; } = 3;

    public static bool TrySelect(Archive archive, Options options, out SessionSummary? session)
    {
        session = null;
        SessionTopic? topic = null;
        if (options.Value("topic") is { } topicWords && !TryTopic(archive, topicWords, options, out topic))
        {
            return false;
        }

        string words = options.Value("session") ?? options.Value("id") ?? string.Join(' ', options.Positional);
        Selection selection = archive.Select(words, topic);
        if (selection.Session != null)
        {
            session = selection.Session;
            return true;
        }

        if (selection.Ambiguous)
        {
            LastExit = 2;
            if (options.Json)
            {
                var json = new JsonObject
                {
                    ["error"] = selection.Problem,
                    ["candidates"] = new JsonArray(selection.Candidates.Select(c => (JsonNode)Render.SessionHeader(archive, c)).ToArray())
                };
                Console.WriteLine(json.ToJsonString(Render.JsonOutput));
            }
            else
            {
                Console.Error.WriteLine(Render.CandidatesText(archive, selection));
            }

            return false;
        }

        LastExit = 3;
        Report(options, selection.Problem);
        return false;
    }

    public static bool TryTopic(Archive archive, string words, Options options, out SessionTopic? topic)
    {
        topic = archive.FindTopic(words, out IReadOnlyList<SessionTopic> candidates);
        if (topic != null)
        {
            return true;
        }

        LastExit = candidates.Count > 1 ? 2 : 3;
        Report(options, candidates.Count > 1
            ? $"{candidates.Count} topics match «{words}»: " + string.Join(", ", candidates.Select(c => c.Name))
            : $"No topic «{words}». Topics: " + string.Join(", ", archive.Topics.Select(t => t.Name)));
        return false;
    }

    private static void Report(Options options, string problem)
    {
        if (options.Json)
        {
            Console.WriteLine(new JsonObject { ["error"] = problem }.ToJsonString(Render.JsonOutput));
            return;
        }

        Console.Error.WriteLine(problem);
    }
}

/// <summary>«list --topic KEGOC --json "разбор demo"»: the command, its words, --name value pairs and flags.</summary>
internal sealed class Options
{
    private static readonly HashSet<string> Flags = ["json", "no-transcript"];
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public string Command { get; private set; } = string.Empty;
    public List<string> Positional { get; } = [];
    public bool Json => _flags.Contains("json");

    public bool Flag(string name) => _flags.Contains(name);

    public string? Value(string name) => _values.TryGetValue(name, out string? value) ? value : null;

    public static Options Parse(string[] args)
    {
        var options = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (options.Command.Length == 0 && !arg.StartsWith("--", StringComparison.Ordinal))
            {
                options.Command = arg.ToLowerInvariant();
                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2)
            {
                string name = arg[2..];
                int eq = name.IndexOf('=');
                if (eq > 0)
                {
                    options._values[name[..eq]] = name[(eq + 1)..];
                }
                else if (Flags.Contains(name) || i + 1 >= args.Length)
                {
                    options._flags.Add(name);
                }
                else
                {
                    options._values[name] = args[++i];
                }

                continue;
            }

            options.Positional.Add(arg);
        }

        return options;
    }
}

internal static class Help
{
    public static string Version => "replio " + (typeof(Help).Assembly.GetName().Version?.ToString(3) ?? "?");

    public static string Text => Version + " — Replio sessions for AI agents (read-only)\n\n" +
        "  replio list [WORDS] [--topic NAME] [--from DATE] [--to DATE] [--date DAY] [--limit N] [--json]\n" +
        "  replio show SESSION [--topic NAME] [--no-transcript] [--json]\n" +
        "  replio export SESSION [--topic NAME] [--out DIR] [--json]\n" +
        "  replio topics [--json]\n" +
        "  replio topic NAME [--json]\n" +
        "  replio where [--json]\n" +
        "  replio guide          print the agent guide (also in %AppData%\\Replio\\AGENTS.md)\n" +
        "  replio mcp            run as an MCP server over stdio\n\n" +
        "SESSION: last | last-2 | an id (6+ first characters) | \"2026-11-17 14:05\" | \"14:05\" | 2026-11-17 | words of the title.\n" +
        "Exit codes: 0 ok, 1 error, 2 several sessions match (they are listed), 3 nothing found.";
}
