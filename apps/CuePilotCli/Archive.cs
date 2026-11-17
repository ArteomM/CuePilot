using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CuePilotDesktop.Models;
using CuePilotDesktop.Services;

namespace CuePilotCli;

/// <summary>A session picked by the user's words, or why none could be picked.</summary>
public sealed record Selection(SessionSummary? Session, IReadOnlyList<SessionSummary> Candidates, string Problem)
{
    public bool Ambiguous => Session == null && Candidates.Count > 1;
}

/// <summary>
/// CuePilot data as the app saved it, read without locking or changing anything: sessions, topics, screenshots and
/// the parsed topic documents. Every method works on a snapshot taken at construction.
/// </summary>
public sealed class Archive
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly string[] TimeFormats =
    [
        "yyyy-MM-dd HH:mm", "yyyy-MM-dd H:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss",
        "dd.MM.yyyy HH:mm", "d.M.yyyy H:mm", "dd.MM.yyyy H:mm", "dd.MM HH:mm", "d.M H:mm"
    ];

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy", "dd.MM", "d.M"];

    public Archive(string root)
    {
        Root = root;
        Sessions = LoadSessions(Path.Combine(root, "sessions"));
        Topics = LoadTopics(Path.Combine(root, "topics.json"));
    }

    public string Root { get; }
    public string SessionsFolder => Path.Combine(Root, "sessions");
    public string ScreenshotsRoot => Path.Combine(Root, "visual-context");
    public string KnowledgeRoot => Path.Combine(Root, "knowledge");
    public string ExportsFolder => Path.Combine(Root, "exports");

    /// <summary>Newest first.</summary>
    public IReadOnlyList<SessionSummary> Sessions { get; }
    public IReadOnlyList<SessionTopic> Topics { get; }

    public string TopicName(SessionSummary session) =>
        Topics.FirstOrDefault(t => t.Id == session.TopicId)?.Name ?? string.Empty;

    public string SessionFile(SessionSummary session) => Path.Combine(SessionsFolder, session.Id + ".json");

    public SessionTopic? FindTopic(string words, out IReadOnlyList<SessionTopic> candidates)
    {
        string query = words.Trim();
        candidates = [];
        if (query.Length == 0)
        {
            return null;
        }

        SessionTopic? exact = Topics.FirstOrDefault(t => t.Id == query || string.Equals(t.Name.Trim(), query, StringComparison.CurrentCultureIgnoreCase));
        if (exact != null)
        {
            return exact;
        }

        candidates = Topics.Where(t => Matches(t.Name, query)).ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>Sessions for «list»: newest first, filtered by words, topic and time.</summary>
    public IEnumerable<SessionSummary> Filter(string? query, SessionTopic? topic, DateTime? from, DateTime? to)
    {
        IEnumerable<SessionSummary> result = Sessions;
        if (topic != null)
        {
            result = result.Where(s => s.TopicId == topic.Id);
        }

        if (from is { } start)
        {
            result = result.Where(s => (s.EndedAt ?? s.StartedAt) >= start);
        }

        if (to is { } end)
        {
            result = result.Where(s => s.StartedAt <= end);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            result = result.Where(s => Matches(s.Title, query) || Matches(TopicName(s) + " " + s.Title, query));
        }

        return result;
    }

    /// <summary>
    /// «last», «last-2», an id or its start, a time («2026-11-17 14:05», «14:05», «17.11.2026»), or words of the title.
    /// </summary>
    public Selection Select(string words, SessionTopic? topic = null)
    {
        string query = words.Trim();
        List<SessionSummary> pool = topic == null ? Sessions.ToList() : Sessions.Where(s => s.TopicId == topic.Id).ToList();
        if (pool.Count == 0)
        {
            return new Selection(null, [], topic == null ? "There are no sessions yet." : $"Topic «{topic.Name}» has no sessions.");
        }

        if (query.Length == 0)
        {
            return new Selection(null, [], "Name a session: last, an id, a time or words of its title.");
        }

        Match last = Regex.Match(query, @"^(?:last|latest|последн\w*)(?:\s*-\s*(\d+))?$", RegexOptions.IgnoreCase);
        if (last.Success)
        {
            int index = last.Groups[1].Success ? int.Parse(last.Groups[1].Value, CultureInfo.InvariantCulture) - 1 : 0;
            return index >= 0 && index < pool.Count
                ? new Selection(pool[index], [], string.Empty)
                : new Selection(null, [], $"Only {pool.Count} session(s) here.");
        }

        SessionSummary? byId = pool.FirstOrDefault(s => s.Id.Equals(query, StringComparison.OrdinalIgnoreCase));
        if (byId != null)
        {
            return new Selection(byId, [], string.Empty);
        }

        if (Regex.IsMatch(query, "^[0-9a-fA-F]{6,32}$"))
        {
            List<SessionSummary> byPrefix = pool.Where(s => s.Id.StartsWith(query, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byPrefix.Count > 0)
            {
                return Pick(byPrefix, $"id «{query}»");
            }
        }

        if (TryParseTime(query, out DateTime moment))
        {
            // The session running at that moment, else the one that started nearest to it within an hour.
            List<SessionSummary> running = pool
                .Where(s => s.StartedAt.AddMinutes(-2) <= moment && moment <= (s.EndedAt ?? s.StartedAt).AddMinutes(2))
                .ToList();
            if (running.Count > 0)
            {
                return Pick(running, query);
            }

            SessionSummary? nearest = pool.MinBy(s => Math.Abs((s.StartedAt - moment).TotalMinutes));
            return nearest != null && Math.Abs((nearest.StartedAt - moment).TotalMinutes) <= 60
                ? new Selection(nearest, [], string.Empty)
                : new Selection(null, [], $"No session at {moment:yyyy-MM-dd HH:mm}.");
        }

        if (TryParseDate(query, out DateTime day))
        {
            List<SessionSummary> sameDay = pool.Where(s => s.StartedAt.Date == day.Date).ToList();
            return sameDay.Count == 0 ? new Selection(null, [], $"No session on {day:yyyy-MM-dd}.") : Pick(sameDay, query);
        }

        List<SessionSummary> exact = pool.Where(s => string.Equals(Normalize(s.Title), Normalize(query), StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (exact.Count > 0)
        {
            // The same name twice (a weekly call): the newest is meant.
            return new Selection(exact[0], [], string.Empty);
        }

        // «KEGOC DEMO5» finds «KEGOC. Разбор DEMO5»: every word somewhere in the topic and title.
        List<SessionSummary> byWords = pool.Where(s => Matches(s.Title, query) || Matches(TopicName(s) + " " + s.Title, query)).ToList();
        if (byWords.Count == 0)
        {
            return new Selection(null, [], $"No session matches «{query}».");
        }

        return Pick(byWords, $"«{query}»");
    }

    public static bool TryParseTime(string text, out DateTime moment)
    {
        string value = text.Trim();
        if (DateTime.TryParseExact(value, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out moment))
        {
            return true;
        }

        if (Regex.IsMatch(value, @"^\d{1,2}:\d{2}$") &&
            DateTime.TryParseExact(value, ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime clock))
        {
            moment = DateTime.Today.Add(clock.TimeOfDay);
            return true;
        }

        return false;
    }

    public static bool TryParseDate(string text, out DateTime day) =>
        DateTime.TryParseExact(text.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out day) ||
        ParseRelativeDay(text, out day);

    /// <summary>A date for --from/--to: «2026-11-17», «17.11.2026 14:00», «today», «yesterday».</summary>
    public static bool TryParseBound(string text, bool end, out DateTime value)
    {
        if (TryParseTime(text, out value))
        {
            return true;
        }

        if (TryParseDate(text, out value))
        {
            value = end ? value.Date.AddDays(1).AddTicks(-1) : value.Date;
            return true;
        }

        return false;
    }

    public string ScreenshotFolder(SessionSummary session) => SessionScreenshots.Folder(ScreenshotsRoot, session.Id);

    private static Selection Pick(List<SessionSummary> matches, string what) =>
        matches.Count == 1
            ? new Selection(matches[0], [], string.Empty)
            : new Selection(null, matches, $"{matches.Count} sessions match {what}; name one by its id.");

    private static bool ParseRelativeDay(string text, out DateTime day)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "today":
            case "сегодня":
                day = DateTime.Today;
                return true;
            case "yesterday":
            case "вчера":
                day = DateTime.Today.AddDays(-1);
                return true;
            default:
                day = default;
                return false;
        }
    }

    /// <summary>Every word of the query is in the text, ignoring case, punctuation and ё/е.</summary>
    private static bool Matches(string text, string query)
    {
        string haystack = Normalize(text);
        string[] words = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 && words.All(w => haystack.Contains(w, StringComparison.CurrentCultureIgnoreCase));
    }

    private static string Normalize(string text) =>
        Regex.Replace(text.ToLowerInvariant().Replace('ё', 'е'), @"[^\p{L}\p{N}]+", " ").Trim();

    private static IReadOnlyList<SessionSummary> LoadSessions(string folder)
    {
        var sessions = new List<SessionSummary>();
        if (!Directory.Exists(folder))
        {
            return sessions;
        }

        foreach (string path in Directory.EnumerateFiles(folder, "*.json"))
        {
            try
            {
                // The app may be saving the same file: share it and never lock.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                SessionSummary? session = JsonSerializer.Deserialize<SessionSummary>(stream, JsonOptions);
                if (session != null && !string.IsNullOrWhiteSpace(session.Id))
                {
                    sessions.Add(session);
                }
            }
            catch (Exception)
            {
                // A file being written or broken: skip it, like the app does.
            }
        }

        return sessions.OrderByDescending(s => s.StartedAt).ToList();
    }

    private static IReadOnlyList<SessionTopic> LoadTopics(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<List<SessionTopic>>(stream, JsonOptions) ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }
}
