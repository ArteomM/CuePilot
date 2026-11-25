using System.IO;
using System.Text.RegularExpressions;
using ReplioDesktop.Models;

namespace ReplioDesktop.Services;

public enum SearchHitKind
{
    Topic,
    TopicSummary,
    Session,
    KnowledgeSource,
    Question
}

/// <summary>Where in a meeting the words were found: the result opens that tab.</summary>
public enum SearchField
{
    Name,
    Summary,
    Transcript,
    Email,
    Dialog,
    Screen,
    Text
}

public sealed record SearchHit(
    SearchHitKind Kind,
    SearchField Field,
    string Title,
    string Snippet,
    string TopicId,
    string SessionId,
    string SourceId,
    DateTime? AskedAt,
    DateTime When);

/// <summary>
/// The search box looks through everything Replio keeps: topics (names and «Сводка»), meetings (titles, summaries,
/// transcripts, follow-up emails, hints, text from screenshots), the knowledge base (titles, notes and the parsed
/// Markdown of each source) and the topics' questions and answers. Every word of the query must be found (in any
/// order, case ignored). Runs off the UI thread; parsed Markdown is cached until its file changes.
/// </summary>
public sealed class AppSearch
{
    private const int SnippetRadius = 70;
    private readonly Func<string, string, string> _sourcePath;
    private readonly Dictionary<string, (DateTime Stamp, string Text)> _sourceCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheSync = new();

    /// <param name="sourcePath">(topicId, sourceId) → the parsed Markdown file of a knowledge source.</param>
    public AppSearch(Func<string, string, string> sourcePath)
    {
        _sourcePath = sourcePath;
    }

    public static string[] Words(string query) =>
        Regex.Split(Fold(query.Trim()), @"\s+").Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>«ё» and «е» are the same letter for the search.</summary>
    private static string Fold(string text) => text.IndexOf('ё') < 0 && text.IndexOf('Ё') < 0 ? text : text.Replace('ё', 'е').Replace('Ё', 'Е');

    public List<SearchHit> Run(string query, IReadOnlyList<SessionSummary> sessions, IReadOnlyList<SessionTopic> topics, CancellationToken token)
    {
        string[] words = Words(query);
        var hits = new List<SearchHit>();
        if (words.Length == 0)
        {
            return hits;
        }

        foreach (SessionTopic topic in topics)
        {
            token.ThrowIfCancellationRequested();
            if (HasAll(topic.Name, words))
            {
                hits.Add(new SearchHit(SearchHitKind.Topic, SearchField.Name, topic.Name, string.Empty, topic.Id, string.Empty, string.Empty, null, topic.CreatedAt));
            }

            if (HasAll(topic.KnowledgeText, words))
            {
                hits.Add(new SearchHit(SearchHitKind.TopicSummary, SearchField.Summary, topic.Name, Snippet(topic.KnowledgeText, words), topic.Id, string.Empty, string.Empty, null,
                    topic.KnowledgeUpdatedAt ?? topic.CreatedAt));
            }

            foreach (KnowledgeSource source in topic.Sources)
            {
                token.ThrowIfCancellationRequested();
                string parsed = ReadSource(topic.Id, source.Id);
                string text = string.IsNullOrWhiteSpace(parsed) ? source.NoteText : parsed;
                if (HasAll(source.Title + "\n" + text, words))
                {
                    string snippet = HasAll(source.Title, words) ? FirstLine(text) : Snippet(text, words);
                    hits.Add(new SearchHit(SearchHitKind.KnowledgeSource, SearchField.Text, source.Title, snippet, topic.Id, string.Empty, source.Id, null,
                        source.ParsedAt ?? source.AddedAt));
                }
            }

            foreach (TopicQuestionEntry question in topic.Questions)
            {
                if (HasAll(question.Question + "\n" + question.Answer, words))
                {
                    string snippet = HasAll(question.Question, words) ? FirstLine(question.Answer) : Snippet(question.Answer, words);
                    hits.Add(new SearchHit(SearchHitKind.Question, SearchField.Text, question.Question, snippet, topic.Id, string.Empty, string.Empty, question.AskedAt, question.AskedAt));
                }
            }
        }

        foreach (SessionSummary session in sessions)
        {
            token.ThrowIfCancellationRequested();
            SearchHit? hit = MatchSession(session, words);
            if (hit != null)
            {
                hits.Add(hit);
            }
        }

        return hits
            .OrderBy(h => h.Kind == SearchHitKind.Topic ? 0 : h.Kind == SearchHitKind.Session ? 1 : h.Kind == SearchHitKind.TopicSummary ? 2 : h.Kind == SearchHitKind.KnowledgeSource ? 3 : 4)
            .ThenByDescending(h => h.When)
            .ToList();
    }

    private static SearchHit? MatchSession(SessionSummary session, string[] words)
    {
        string transcript = string.Join("\n", session.Transcript.Select(t => string.IsNullOrWhiteSpace(t.Text) ? t.RawText : t.Text));
        string dialog = string.Join("\n", session.AssistDialog.Select(m => m.Text));
        string screen = string.Join("\n", session.VisualFrames.Select(f => f.WindowTitle + " " + f.OcrText));
        string summary = SessionScreenshots.StripLinks(session.SummaryText ?? string.Empty);
        var fields = new (SearchField Field, string Text)[]
        {
            (SearchField.Name, session.Title + " " + session.ModeName),
            (SearchField.Summary, summary),
            (SearchField.Transcript, transcript),
            (SearchField.Email, session.FollowUpEmailText),
            (SearchField.Dialog, dialog),
            (SearchField.Screen, screen)
        };

        foreach ((SearchField field, string text) in fields)
        {
            if (HasAll(text, words))
            {
                string snippet = field == SearchField.Name ? FirstLine(summary.Length > 0 ? summary : transcript) : Snippet(text, words);
                return new SearchHit(SearchHitKind.Session, field, session.Title, snippet, session.TopicId, session.Id, string.Empty, null, session.StartedAt);
            }
        }

        // The words are spread over the meeting: title in one place, a name in the transcript…
        string all = string.Join("\n", fields.Select(f => f.Text));
        if (!HasAll(all, words))
        {
            return null;
        }

        // Shown and opened: the part (other than the title) that holds most of the words.
        (SearchField best, string bestText) = fields
            .Skip(1)
            .OrderByDescending(f => words.Count(w => Fold(f.Text).Contains(w, StringComparison.OrdinalIgnoreCase)))
            .First();
        return new SearchHit(SearchHitKind.Session, best, session.Title, Snippet(bestText, words), session.TopicId, session.Id, string.Empty, null, session.StartedAt);
    }

    private string ReadSource(string topicId, string sourceId)
    {
        try
        {
            string path = _sourcePath(topicId, sourceId);
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            DateTime stamp = File.GetLastWriteTimeUtc(path);
            lock (_cacheSync)
            {
                if (_sourceCache.TryGetValue(path, out var cached) && cached.Stamp == stamp)
                {
                    return cached.Text;
                }
            }

            string text = File.ReadAllText(path);
            lock (_cacheSync)
            {
                _sourceCache[path] = (stamp, text);
            }

            return text;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static bool HasAll(string? text, string[] words)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        text = Fold(text);
        foreach (string word in words)
        {
            if (text.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>One line around the first word found, without Markdown marks.</summary>
    public static string Snippet(string text, string[] words)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        int at = -1;
        string folded = Fold(text);
        foreach (string word in words)
        {
            at = folded.IndexOf(word, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                break;
            }
        }

        if (at < 0)
        {
            return FirstLine(text);
        }

        int start = Math.Max(0, at - SnippetRadius);
        int end = Math.Min(text.Length, at + SnippetRadius * 2);
        string piece = Clean(text[start..end]);
        return (start > 0 ? "…" : string.Empty) + piece + (end < text.Length ? "…" : string.Empty);
    }

    private static string FirstLine(string? text)
    {
        string clean = Clean(text ?? string.Empty);
        return clean.Length <= SnippetRadius * 3 ? clean : clean[..(SnippetRadius * 3)] + "…";
    }

    private static string Clean(string text)
    {
        // Links keep their text; checkboxes «- [ ]» and list marks go, then the remaining Markdown marks.
        string flat = Regex.Replace(text, @"!?\[([^\]]*)\]\([^)]*\)", "$1");
        flat = Regex.Replace(flat, @"\[[ xX]\]", " ");
        flat = Regex.Replace(flat, @"(^|\n)[ \t]*(?:[-+•]|\d+[.)])[ \t]+", "$1");
        flat = Regex.Replace(flat, @"[#*_`>|]+", " ");
        // A heading and the item under it stay apart: «Задачи · Черновики трёх кейсов».
        flat = Regex.Replace(flat, @"[ \t]*\r?\n\s*", " · ");
        flat = Regex.Replace(flat, @"\s+", " ");
        flat = Regex.Replace(flat, @"(?: · )+", " · ");
        return flat.Trim().Trim('·').Trim();
    }
}
