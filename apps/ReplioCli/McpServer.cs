using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReplioDesktop.Models;

namespace ReplioCli;

/// <summary>
/// «replio mcp»: a Model Context Protocol server over stdio (newline-delimited JSON-RPC 2.0), so Claude Code,
/// Claude Desktop, Cursor and other MCP clients can list, read and export Replio sessions as tools.
/// Data is re-read on every call, so a session that just ended is there at once.
/// </summary>
public static class McpServer
{
    private const string LatestProtocol = "2025-06-18";
    private const int MaxImageBytes = 4 * 1024 * 1024;

    // One message per line; Cyrillic stays readable instead of \u escapes (newlines inside strings are still escaped).
    private static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static async Task<int> RunAsync(string root)
    {
        var utf8 = new UTF8Encoding(false);
        using var input = new StreamReader(Console.OpenStandardInput(), utf8);
        using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true, NewLine = "\n" };

        while (await input.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonNode? response;
            try
            {
                response = Handle(root, JsonNode.Parse(line));
            }
            catch (JsonException)
            {
                response = Error(null, -32700, "Parse error");
            }

            if (response != null)
            {
                await output.WriteLineAsync(response.ToJsonString(Compact));
            }
        }

        return 0;
    }

    private static JsonNode? Handle(string root, JsonNode? message)
    {
        if (message is not JsonObject request)
        {
            return Error(null, -32600, "Invalid request");
        }

        JsonNode? id = request["id"]?.DeepClone();
        string method = request["method"]?.GetValue<string>() ?? string.Empty;
        if (id == null)
        {
            // A notification («notifications/initialized», «notifications/cancelled»): no answer.
            return null;
        }

        try
        {
            return method switch
            {
                "initialize" => Result(id, Initialize(request["params"] as JsonObject)),
                "ping" => Result(id, new JsonObject()),
                "tools/list" => Result(id, new JsonObject { ["tools"] = Tools() }),
                "tools/call" => Result(id, Call(root, request["params"] as JsonObject)),
                "resources/list" => Result(id, new JsonObject { ["resources"] = new JsonArray() }),
                "prompts/list" => Result(id, new JsonObject { ["prompts"] = new JsonArray() }),
                _ => Error(id, -32601, "Method not found: " + method)
            };
        }
        catch (Exception ex)
        {
            return Error(id, -32603, ex.Message);
        }
    }

    private static JsonObject Initialize(JsonObject? parameters) => new()
    {
        ["protocolVersion"] = parameters?["protocolVersion"]?.GetValue<string>() ?? LatestProtocol,
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
        ["serverInfo"] = new JsonObject { ["name"] = "replio", ["version"] = Help.Version.Replace("replio ", string.Empty) },
        ["instructions"] =
            "Replio meeting sessions on this computer: transcript, AI summary (Markdown, Russian headings), screenshots, topics " +
            "(shown to users as «Группы», groups of meetings). " +
            "Find a session with list_sessions (by words, topic or dates), then read it with get_session. " +
            "A session can be named by id, by «last», by a time like «2026-11-17 14:05» or by words of its title. Read-only."
    };

    private static JsonArray Tools() =>
    [
        Tool("list_sessions",
            "List Replio sessions, newest first. Filter by words of the title or topic, by topic name and by time.",
            new JsonObject
            {
                ["query"] = Text("Words that must all appear in the title or topic name, e.g. «KEGOC demo»."),
                ["topic"] = Text("Topic name (or its id)."),
                ["from"] = Text("Earliest time, e.g. 2026-11-17 or 2026-11-17 09:00 or «today»."),
                ["to"] = Text("Latest time, same formats."),
                ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "At most this many sessions (default 30)." }
            }),
        Tool("get_session",
            "Read one session as Markdown: facts, AI summary, list of screenshots with file paths, and the transcript.",
            SessionArgs(new JsonObject
            {
                ["include_transcript"] = new JsonObject { ["type"] = "boolean", ["description"] = "Include the transcript (default true)." }
            }), required: ["session"]),
        Tool("export_session",
            "Export a session into a folder (session.md, summary.md, transcript.md, screenshots/, session.json) and return its path.",
            SessionArgs(new JsonObject
            {
                ["out_dir"] = Text("Parent folder for the export (default: the Replio exports folder).")
            }), required: ["session"]),
        Tool("get_screenshot",
            "Return a screenshot of a session as an image. Numbers are the «снимок N» numbers of get_session.",
            SessionArgs(new JsonObject
            {
                ["number"] = new JsonObject { ["type"] = "integer", ["description"] = "Screenshot number, 1 for the first." }
            }), required: ["session", "number"]),
        Tool("list_topics", "List topics (groups of sessions with a shared overview and documents).", new JsonObject()),
        Tool("get_topic",
            "Read a topic: its overview built from all its meetings and documents, its sessions, and the paths of its parsed documents.",
            new JsonObject { ["topic"] = Text("Topic name or id.") }, required: ["topic"])
    ];

    private static JsonObject SessionArgs(JsonObject extra)
    {
        var properties = new JsonObject
        {
            ["session"] = Text("«last», «last-2», an id or its first 6+ characters, a time («2026-11-17 14:05», «14:05», «2026-11-17») or words of the title."),
            ["topic"] = Text("Look only inside this topic (name or id).")
        };
        foreach (KeyValuePair<string, JsonNode?> pair in extra)
        {
            properties[pair.Key] = pair.Value?.DeepClone();
        }

        return properties;
    }

    private static JsonObject Call(string root, JsonObject? parameters)
    {
        string name = parameters?["name"]?.GetValue<string>() ?? string.Empty;
        JsonObject args = parameters?["arguments"] as JsonObject ?? new JsonObject();
        var archive = new Archive(root);
        return name switch
        {
            "list_sessions" => ListSessions(archive, args),
            "get_session" => WithSession(archive, args, s => TextResult(
                Render.SessionMarkdown(archive, s, Bool(args, "include_transcript", true), path => new Uri(Path.GetFullPath(path)).AbsoluteUri))),
            "export_session" => WithSession(archive, args, s =>
            {
                Exporter.Result result = Exporter.Export(archive, s, Str(args, "out_dir"));
                return TextResult($"Exported «{s.Title}» to {result.Folder}\n" + string.Join('\n', result.Files));
            }),
            "get_screenshot" => WithSession(archive, args, s => Screenshot(s, Int(args, "number"))),
            "list_topics" => TextResult(Render.TopicsText(archive)),
            "get_topic" => GetTopic(archive, args),
            _ => TextResult("Unknown tool: " + name, error: true)
        };
    }

    private static JsonObject ListSessions(Archive archive, JsonObject args)
    {
        SessionTopic? topic = null;
        if (Str(args, "topic") is { } topicWords)
        {
            topic = archive.FindTopic(topicWords, out _);
            if (topic == null)
            {
                return TextResult($"No topic «{topicWords}». Topics: " + string.Join(", ", archive.Topics.Select(t => t.Name)), error: true);
            }
        }

        DateTime? from = Str(args, "from") is { } f && Archive.TryParseBound(f, false, out DateTime a) ? a : null;
        DateTime? to = Str(args, "to") is { } t && Archive.TryParseBound(t, true, out DateTime b) ? b : null;
        int limit = Int(args, "limit") is > 0 and var n ? n : 30;
        List<SessionSummary> sessions = archive.Filter(Str(args, "query"), topic, from, to).Take(limit).ToList();
        return TextResult(Render.ListText(archive, sessions) + "\n\n" + Render.ListJson(archive, sessions));
    }

    private static JsonObject GetTopic(Archive archive, JsonObject args)
    {
        string words = Str(args, "topic") ?? string.Empty;
        SessionTopic? topic = archive.FindTopic(words, out IReadOnlyList<SessionTopic> candidates);
        if (topic == null)
        {
            return TextResult(candidates.Count > 1
                ? "Several topics match: " + string.Join(", ", candidates.Select(c => c.Name))
                : $"No topic «{words}». Topics: " + string.Join(", ", archive.Topics.Select(t => t.Name)), error: true);
        }

        return TextResult(Render.TopicMarkdown(archive, topic));
    }

    private static JsonObject WithSession(Archive archive, JsonObject args, Func<SessionSummary, JsonObject> action)
    {
        SessionTopic? topic = null;
        if (Str(args, "topic") is { } topicWords)
        {
            topic = archive.FindTopic(topicWords, out _);
            if (topic == null)
            {
                return TextResult($"No topic «{topicWords}».", error: true);
            }
        }

        Selection selection = archive.Select(Str(args, "session") ?? string.Empty, topic);
        if (selection.Session != null)
        {
            return action(selection.Session);
        }

        return TextResult(selection.Ambiguous ? Render.CandidatesText(archive, selection) : selection.Problem, error: true);
    }

    private static JsonObject Screenshot(SessionSummary session, int number)
    {
        SessionScreenshot? shot = session.Screenshots.FirstOrDefault(s => s.Number == number);
        if (shot == null || !File.Exists(shot.FilePath))
        {
            return TextResult(session.Screenshots.Count == 0
                ? "This session has no screenshots."
                : $"No screenshot {number}; this session has {string.Join(", ", session.Screenshots.Select(s => s.Number))}.", error: true);
        }

        var info = new FileInfo(shot.FilePath);
        if (info.Length > MaxImageBytes)
        {
            return TextResult($"Screenshot {number} is {info.Length / 1024 / 1024} MB; open it from {shot.FilePath}.");
        }

        string mime = info.Extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg"
        };
        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = $"Screenshot {number} of «{session.Title}», {shot.Timestamp:yyyy-MM-dd HH:mm:ss}" + (string.IsNullOrWhiteSpace(shot.WindowTitle) ? string.Empty : ", window: " + shot.WindowTitle) + $"\n{shot.FilePath}" },
                new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(File.ReadAllBytes(shot.FilePath)), ["mimeType"] = mime }
            }
        };
    }

    private static JsonObject Tool(string name, string description, JsonObject properties, string[]? required = null)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required is { Length: > 0 })
        {
            schema["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
        }

        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = schema,
            ["annotations"] = new JsonObject { ["readOnlyHint"] = name != "export_session" }
        };
    }

    private static JsonObject Text(string description) => new() { ["type"] = "string", ["description"] = description };

    private static JsonObject TextResult(string text, bool error = false)
    {
        var result = new JsonObject { ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } } };
        if (error)
        {
            result["isError"] = true;
        }

        return result;
    }

    private static string? Str(JsonObject args, string name) =>
        args[name] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static int Int(JsonObject args, string name)
    {
        if (args[name] is not JsonValue value)
        {
            return 0;
        }

        if (value.TryGetValue(out int number))
        {
            return number;
        }

        return value.TryGetValue(out string? text) && int.TryParse(text, out int parsed) ? parsed : 0;
    }

    private static bool Bool(JsonObject args, string name, bool fallback) =>
        args[name] is JsonValue value && value.TryGetValue(out bool flag) ? flag : fallback;

    private static JsonObject Result(JsonNode id, JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    };
}
