using System.IO;
using System.Text.Json;
using ReplioDesktop.Models;

namespace ReplioDesktop.Services;

/// <summary>
/// Copies a cloud client (Яндекс Диск, OneDrive, iCloud, Dropbox, Google Drive) leaves when two computers changed
/// the same file before syncing: «topics (2).json», «topics-PC-NAME.json», «abc123 (конфликт).json»…
/// Names differ per cloud, so a copy is recognised by content: a session file whose name is not its own id,
/// or a «topics*.json» next to topics.json.
/// • topics: merged automatically (no data is lost: sources, questions and sessions are united);
/// • sessions: identical copies are tidied away; different versions wait for the user's choice in Settings.
/// Nothing is deleted: replaced versions go to &lt;data folder&gt;\conflicts.
/// </summary>
public static class DataConflicts
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static string ConflictsFolder => Path.Combine(DataLocation.Root, "conflicts");

    /// <summary>Session files are named by the letters and digits of the id (JsonSessionStore.GetSessionPath).</summary>
    public static string FileId(string sessionId) => string.Concat(sessionId.Where(char.IsLetterOrDigit));

    public static bool IsCopyName(string path, string sessionId) =>
        !string.Equals(Path.GetFileNameWithoutExtension(path), FileId(sessionId), StringComparison.OrdinalIgnoreCase);

    /// <summary>Folds «topics*.json» copies into topics.json. Never throws.</summary>
    public static void MergeTopicCopies(string topicsPath)
    {
        try
        {
            string? folder = Path.GetDirectoryName(topicsPath);
            if (folder == null || !Directory.Exists(folder))
            {
                return;
            }

            string mainName = Path.GetFileName(topicsPath);
            var copies = Directory.EnumerateFiles(folder, "topics*.json")
                .Where(p => !string.Equals(Path.GetFileName(p), mainName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (copies.Count == 0)
            {
                return;
            }

            List<SessionTopic> merged = ReadTopics(topicsPath) ?? [];
            bool wroteAny = false;
            foreach (string copy in copies)
            {
                List<SessionTopic>? other = ReadTopics(copy);
                if (other == null)
                {
                    continue;
                }

                merged = MergeTopics(merged, other);
                wroteAny = true;
                MoveAside(copy);
            }

            if (wroteAny)
            {
                string temp = topicsPath + ".tmp";
                File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(merged, WriteOptions));
                File.Move(temp, topicsPath, overwrite: true);
            }
        }
        catch (Exception)
        {
            // The app still starts with topics.json as it is; the copies stay for the next start.
        }
    }

    /// <summary>Union by id; for a topic on both sides the newer summary/graph wins and lists are united.</summary>
    public static List<SessionTopic> MergeTopics(List<SessionTopic> mine, List<SessionTopic> theirs)
    {
        var result = mine.ToList();
        foreach (SessionTopic other in theirs)
        {
            SessionTopic? same = result.FirstOrDefault(t => t.Id == other.Id);
            if (same == null)
            {
                result.Add(other);
                continue;
            }

            if ((other.KnowledgeUpdatedAt ?? DateTime.MinValue) > (same.KnowledgeUpdatedAt ?? DateTime.MinValue))
            {
                same.KnowledgeText = other.KnowledgeText;
                same.KnowledgeUpdatedAt = other.KnowledgeUpdatedAt;
                same.KnowledgeSourcesStamp = other.KnowledgeSourcesStamp;
            }

            if ((other.GraphUpdatedAt ?? DateTime.MinValue) > (same.GraphUpdatedAt ?? DateTime.MinValue))
            {
                same.GraphUpdatedAt = other.GraphUpdatedAt;
                same.GraphStamp = other.GraphStamp;
            }

            if ((other.LastAnswerAt ?? DateTime.MinValue) > (same.LastAnswerAt ?? DateTime.MinValue))
            {
                same.LastQuestion = other.LastQuestion;
                same.LastAnswer = other.LastAnswer;
                same.LastAnswerAt = other.LastAnswerAt;
            }

            if (string.IsNullOrWhiteSpace(same.Name))
            {
                same.Name = other.Name;
            }

            same.KnowledgeSessionIds = same.KnowledgeSessionIds.Union(other.KnowledgeSessionIds).ToList();
            foreach (KnowledgeSource source in other.Sources)
            {
                KnowledgeSource? existing = same.Sources.FirstOrDefault(s => s.Id == source.Id);
                if (existing == null)
                {
                    same.Sources.Add(source);
                }
                else if ((source.ParsedAt ?? DateTime.MinValue) > (existing.ParsedAt ?? DateTime.MinValue))
                {
                    same.Sources[same.Sources.IndexOf(existing)] = source;
                }
            }

            foreach (TopicQuestionEntry question in other.Questions)
            {
                if (!same.Questions.Any(q => q.AskedAt == question.AskedAt && q.Question == question.Question))
                {
                    same.Questions.Add(question);
                }
            }

            same.Questions = same.Questions.OrderBy(q => q.AskedAt).ToList();
        }

        return result;
    }

    /// <summary>
    /// Session copies: a copy whose main file is missing becomes the main file, an identical copy is moved aside.
    /// Different versions stay for <see cref="ListSessionConflicts"/>. Never throws.
    /// </summary>
    public static void TidySessionCopies(string sessionsFolder)
    {
        try
        {
            foreach (string path in Directory.EnumerateFiles(sessionsFolder, "*.json"))
            {
                string? id = ReadSessionId(path);
                if (id == null || !IsCopyName(path, id))
                {
                    continue;
                }

                string main = Path.Combine(sessionsFolder, FileId(id) + ".json");
                if (!File.Exists(main))
                {
                    File.Move(path, main);
                }
                else if (File.ReadAllBytes(main).AsSpan().SequenceEqual(File.ReadAllBytes(path)))
                {
                    MoveAside(path);
                }
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Sessions that have two different versions after a sync.</summary>
    public static List<SessionConflict> ListSessionConflicts()
    {
        var result = new List<SessionConflict>();
        string folder = Path.Combine(DataLocation.Root, "sessions");
        try
        {
            if (!Directory.Exists(folder))
            {
                return result;
            }

            foreach (string path in Directory.EnumerateFiles(folder, "*.json"))
            {
                SessionSummary? copy = ReadSession(path);
                if (copy == null || !IsCopyName(path, copy.Id))
                {
                    continue;
                }

                string main = Path.Combine(folder, FileId(copy.Id) + ".json");
                SessionSummary? current = ReadSession(main);
                if (current == null)
                {
                    continue;
                }

                result.Add(new SessionConflict(
                    copy.Id,
                    string.IsNullOrWhiteSpace(current.Title) ? copy.Title : current.Title,
                    main,
                    path,
                    Describe(current, main),
                    Describe(copy, path)));
            }
        }
        catch (Exception)
        {
        }

        return result;
    }

    /// <summary>Keeps one version as the session file; the other goes to the conflicts folder.</summary>
    public static void Resolve(SessionConflict conflict, bool keepCopy)
    {
        if (keepCopy)
        {
            MoveAside(conflict.MainPath);
            File.Move(conflict.CopyPath, conflict.MainPath);
        }
        else
        {
            MoveAside(conflict.CopyPath);
        }
    }

    private static SessionVersionInfo Describe(SessionSummary session, string path) =>
        new(File.GetLastWriteTime(path), session.Transcript.Count, !string.IsNullOrWhiteSpace(session.SummaryText));

    private static void MoveAside(string path)
    {
        Directory.CreateDirectory(ConflictsFolder);
        string name = Path.GetFileNameWithoutExtension(path) + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + Path.GetExtension(path);
        File.Move(path, Path.Combine(ConflictsFolder, name), overwrite: true);
    }

    private static List<SessionTopic>? ReadTopics(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using FileStream stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<List<SessionTopic>>(stream, ReadOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static SessionSummary? ReadSession(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using FileStream stream = File.OpenRead(path);
            SessionSummary? session = JsonSerializer.Deserialize<SessionSummary>(stream, ReadOptions);
            return session != null && !string.IsNullOrWhiteSpace(session.Id) ? session : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ReadSessionId(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream);
            return document.RootElement.TryGetProperty("Id", out JsonElement id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Two versions of one session after a sync.</summary>
public sealed record SessionConflict(string SessionId, string Title, string MainPath, string CopyPath, SessionVersionInfo Main, SessionVersionInfo Copy);

/// <summary>What tells the versions apart for the user: when the file changed, transcript lines, a summary or not.</summary>
public sealed record SessionVersionInfo(DateTime Changed, int Lines, bool HasSummary);
