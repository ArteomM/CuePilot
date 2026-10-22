using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CuePilotDesktop.Models;

namespace CuePilotDesktop.Services;

/// <summary>A parsed knowledge source (file, folder or note) handed to the topic prompts.</summary>
public sealed record TopicDocument(string Title, string Markdown);

/// <summary>Topic workspace: shared knowledge base, questions about the topic, entity graph, document recognition.</summary>
public partial class ConversationAssistService
{
    /// <summary>Text and tables of a picture (or a scanned page) as Markdown, via the fast vision model.</summary>
    public async Task<AssistResult> RecognizeImageAsync(string imagePath, AssistOptions options, CancellationToken cancellationToken = default)
    {
        string systemPrompt =
            "You convert images of documents, slides, screenshots and photos into Markdown for a knowledge base. " +
            "Transcribe all readable text exactly, in its original language; keep headings, lists and tables (as Markdown tables). " +
            "Do not translate and do not invent text that is not visible.";
        string userPrompt =
            "Convert this image to Markdown.\n" +
            "1. All visible text, in reading order.\n" +
            "2. Then a line \"**На изображении:** ...\" with one or two sentences about what the picture shows (diagram, chart, photo, people), written in Russian.\n" +
            "No preamble.";
        LlmResponse response = await CompleteAsync(
            options,
            AssistModelTier.Fast,
            systemPrompt,
            userPrompt,
            [new AssistImageInput { FilePath = imagePath, Detail = "high" }],
            cancellationToken);
        return AssistResult.From(response, response.Text.Trim());
    }

    /// <summary>
    /// Builds a topic's shared knowledge base from its sessions (summaries, or transcripts when there is none)
    /// and the documents, folders and notes attached to it.
    /// </summary>
    public async Task<AssistResult> BuildTopicKnowledgeAsync(
        string topicName,
        IReadOnlyList<SessionSummary> sessions,
        IReadOnlyList<TopicDocument> documents,
        AssistOptions options,
        CancellationToken cancellationToken = default)
    {
        var context = new StringBuilder();
        // Keep the request bounded: every session and document is capped, and the total stops growing past the limit.
        const int maxCharsPerSession = 6000;
        const int maxSessionChars = 50000;
        foreach (SessionSummary session in sessions.OrderBy(s => s.StartedAt))
        {
            string body = string.IsNullOrWhiteSpace(session.SummaryText)
                ? BuildTranscriptContext(session, null, 120)
                : session.SummaryText;
            context.Append("### ").Append(session.Title).Append(" (").Append(session.StartedAt.ToString("yyyy-MM-dd HH:mm")).AppendLine(")");
            context.AppendLine(Cap(body.Trim(), maxCharsPerSession));
            context.AppendLine();
            if (context.Length > maxSessionChars)
            {
                break;
            }
        }

        string documentContext = BuildDocumentOverview(documents, 40000);
        string systemPrompt =
            "You maintain a knowledge base for a topic: a series of related meetings plus documents attached to it. " +
            "Merge facts across all sessions and documents, resolve contradictions in favour of the most recent session, " +
            "be factual and terse, keep names, numbers and terminology exactly as given. Write in Russian.";
        string fullPrompt =
            $"Topic: {topicName}\n" +
            $"Sessions in this topic (oldest first): {sessions.Count}\n\n" +
            (sessions.Count > 0 ? context.ToString() : "(no sessions yet)\n") + "\n" +
            $"Attached documents and notes: {documents.Count}\n\n" +
            (documents.Count > 0 ? documentContext : "(none)\n") + "\n" +
            "Produce the shared knowledge base in exactly this Markdown structure (keep the headings exactly as written):\n\n" +
            "## Общий контекст\n<3-5 sentences: what this topic is about and where it stands now>\n\n" +
            "## Ключевые факты и решения\n- ...\n\n" +
            "## Цифры и данные из документов\n- <figure, date or requirement> (документ)\n\n" +
            "## Открытые вопросы и задачи\n- <task> (ответственный, срок — if known)\n\n" +
            "## Участники\n- <Name>: <role and positions>\n\n" +
            "## Хронология\n- <date>: <what happened in that session, one line>\n\n" +
            "Rules: no preamble, bullets <= 30 words, write in Russian, omit a section's bullets only if nothing is known (write \"—\").";

        LlmResponse response = await CompleteAsync(options, AssistModelTier.Analysis, systemPrompt, fullPrompt, [], cancellationToken);
        return AssistResult.From(response, response.Text.Trim());
    }

    /// <summary>
    /// Answers a question about a topic from the current state of its sessions and attached documents,
    /// not only the saved knowledge base, and flags conflicting statements of different people.
    /// Earlier questions of the dialog are passed so follow-up questions make sense.
    /// </summary>
    public async Task<AssistResult> AskTopicAsync(
        string topicName,
        string knowledgeText,
        IReadOnlyList<SessionSummary> sessions,
        IReadOnlyList<TopicDocument> documents,
        IReadOnlyList<TopicQuestionEntry> history,
        string question,
        AssistOptions options,
        CancellationToken cancellationToken = default)
    {
        const int maxCharsPerSummary = 5000;
        const int maxSessionChars = 60000;
        const int recentSessionsWithTranscript = 4;
        List<SessionSummary> newestFirst = sessions.OrderByDescending(s => s.StartedAt).ToList();
        var context = new StringBuilder();
        for (int i = 0; i < newestFirst.Count && context.Length < maxSessionChars; i++)
        {
            SessionSummary session = newestFirst[i];
            bool recent = i < recentSessionsWithTranscript;
            context.Append("### [S").Append(i + 1).Append("] ").Append(session.Title)
                .Append(" (").Append(session.StartedAt.ToString("yyyy-MM-dd HH:mm")).AppendLine(")");
            if (!string.IsNullOrWhiteSpace(session.SummaryText))
            {
                context.AppendLine("Summary:").AppendLine(Cap(session.SummaryText.Trim(), maxCharsPerSummary));
            }

            if (recent || string.IsNullOrWhiteSpace(session.SummaryText))
            {
                // Speaker-attributed lines let the model quote who said what; the question picks the relevant ones.
                context.AppendLine("Transcript excerpts:").AppendLine(BuildTranscriptContext(session, question, recent ? 80 : 40).TrimEnd());
            }

            context.AppendLine();
        }

        string documentContext = BuildDocumentExcerpts(documents, question, 30000);
        var dialog = new StringBuilder();
        foreach (TopicQuestionEntry entry in history.TakeLast(3))
        {
            dialog.Append("Q: ").AppendLine(entry.Question.Trim());
            dialog.Append("A: ").AppendLine(Cap(entry.Answer.Trim(), 1500));
            dialog.AppendLine();
        }

        bool english = IsEnglish(options.Settings);
        string conflictsHeading = english ? "Discrepancies" : "Расхождения";
        string clarifyHeading = english ? "To clarify" : "Что уточнить";
        string systemPrompt =
            "You answer questions about a topic (a series of related meetings plus attached documents) using only the data provided. " +
            "Sessions are listed newest first and labelled [S1], [S2], …; [S1] is the most recent. Documents are labelled [D1], [D2], …. " +
            "The saved knowledge base may be outdated: when it disagrees with session data, trust the session data and the most recent session. " +
            "Keep names, numbers and terminology exactly as given. Do not invent facts; if the data does not answer the question, say so. Write in Russian.";
        string fullPrompt =
            $"Topic: {topicName}\n" +
            $"Today: {DateTime.Now:yyyy-MM-dd HH:mm}\n" +
            $"Sessions in this topic: {sessions.Count}; documents: {documents.Count}\n\n" +
            "Saved knowledge base (may be outdated):\n" +
            (string.IsNullOrWhiteSpace(knowledgeText) ? "(none)" : Cap(knowledgeText.Trim(), 12000)) + "\n\n" +
            "Session data (newest first):\n" +
            (context.Length > 0 ? context.ToString() : "(no sessions)\n") + "\n" +
            "Document excerpts relevant to the question:\n" +
            (documentContext.Length > 0 ? documentContext : "(none)\n") + "\n" +
            (dialog.Length > 0 ? "Earlier questions in this dialog (for context only):\n" + dialog + "\n" : string.Empty) +
            $"Question: {question.Trim()}\n\n" +
            "Answer format:\n" +
            "1. A direct answer first (2-6 sentences or a short list), reflecting the latest state. After each fact add its source as (S<n>, <date>) or (D<n>).\n" +
            "2. If different people made conflicting or inconsistent statements relevant to the question — between sessions, " +
            "especially the most recent ones, within one session, or between a meeting and a document — add a section \"## ⚠ " + conflictsHeading + "\" " +
            "with one bullet per conflict: who said what, in which session or document and when, and which statement is the latest. " +
            "If something was later changed by a decision, say so. Omit this section entirely when there are no conflicts.\n" +
            "3. If the answer depends on facts that are missing or unclear, add a short section \"## " + clarifyHeading + "\". Omit it otherwise.\n" +
            "No preamble, plain Markdown, bullets <= 35 words, write in Russian.";

        LlmResponse response = await CompleteAsync(options, AssistModelTier.Analysis, systemPrompt, fullPrompt, [], cancellationToken);
        return AssistResult.From(response, response.Text.Trim());
    }

    /// <summary>Entities (people, organizations, projects, tasks, decisions, documents, terms) and their relations, as JSON.</summary>
    public async Task<(TopicGraph Graph, AssistResult Result)> BuildTopicGraphAsync(
        string topicName,
        string knowledgeText,
        IReadOnlyList<SessionSummary> sessions,
        IReadOnlyList<TopicDocument> documents,
        AssistOptions options,
        CancellationToken cancellationToken = default)
    {
        var context = new StringBuilder();
        foreach (SessionSummary session in sessions.OrderBy(s => s.StartedAt))
        {
            string body = string.IsNullOrWhiteSpace(session.SummaryText)
                ? BuildTranscriptContext(session, null, 60)
                : session.SummaryText;
            context.Append("### Session: ").Append(session.Title).Append(" (").Append(session.StartedAt.ToString("yyyy-MM-dd")).AppendLine(")");
            context.AppendLine(Cap(body.Trim(), 4000)).AppendLine();
            if (context.Length > 45000)
            {
                break;
            }
        }

        string systemPrompt =
            "You build a knowledge graph of a topic (meetings plus documents), like an Obsidian graph. " +
            "Extract the important entities and the relations between them. Use the names exactly as written in the data. " +
            "Merge duplicates (the same person written differently is one node). Return JSON only.";
        string fullPrompt =
            $"Topic: {topicName}\n\n" +
            "Knowledge base:\n" + (string.IsNullOrWhiteSpace(knowledgeText) ? "(none)" : Cap(knowledgeText.Trim(), 10000)) + "\n\n" +
            "Sessions:\n" + (context.Length > 0 ? context.ToString() : "(none)\n") + "\n" +
            "Documents:\n" + (documents.Count > 0 ? BuildDocumentOverview(documents, 30000) : "(none)\n") + "\n" +
            "Return a JSON object:\n" +
            "{\"nodes\":[{\"id\":\"n1\",\"label\":\"<short name>\",\"type\":\"person|organization|project|task|decision|document|term|date|place\"," +
            "\"description\":\"<one sentence in Russian>\",\"sources\":[\"<exact session title or document name as in the headings, without the [S1]/[D1] mark>\"]}]," +
            "\"edges\":[{\"source\":\"n1\",\"target\":\"n2\",\"label\":\"<relation, 1-3 words in Russian>\"}]}\n" +
            "Rules: 15-70 nodes ranked by importance; every node has at least one edge; no edges to missing ids; " +
            "labels <= 40 characters; documents attached to the topic are nodes of type document; JSON only, no Markdown fences.";

        LlmResponse response = await CompleteAsync(options, AssistModelTier.Analysis, systemPrompt, fullPrompt, [], cancellationToken);
        TopicGraph graph = ParseGraph(response.Text);
        return (graph, AssistResult.From(response, response.Text));
    }

    private static TopicGraph ParseGraph(string text)
    {
        string json = text.Trim();
        int start = json.IndexOf('{');
        int end = json.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException("Модель не вернула граф.");
        }

        json = json[start..(end + 1)];
        TopicGraph graph = JsonSerializer.Deserialize<TopicGraph>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Модель не вернула граф.");
        graph.Nodes = graph.Nodes
            .Where(n => !string.IsNullOrWhiteSpace(n.Id) && !string.IsNullOrWhiteSpace(n.Label))
            .GroupBy(n => n.Id)
            .Select(g => g.First())
            .ToList();
        var ids = new HashSet<string>(graph.Nodes.Select(n => n.Id));
        graph.Edges = graph.Edges
            .Where(e => ids.Contains(e.Source) && ids.Contains(e.Target) && e.Source != e.Target)
            .DistinctBy(e => e.Source + "→" + e.Target)
            .ToList();
        return graph;
    }

    /// <summary>The beginning of every document, sharing the budget evenly.</summary>
    private static string BuildDocumentOverview(IReadOnlyList<TopicDocument> documents, int maxChars)
    {
        if (documents.Count == 0)
        {
            return string.Empty;
        }

        int perDocument = Math.Max(1500, maxChars / documents.Count);
        var builder = new StringBuilder();
        for (int i = 0; i < documents.Count && builder.Length < maxChars; i++)
        {
            builder.Append("### [D").Append(i + 1).Append("] ").AppendLine(documents[i].Title);
            builder.AppendLine(Cap(documents[i].Markdown.Trim(), perDocument)).AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>
    /// Passages of the documents that share the most words with the question, plus the opening of each document,
    /// so a question about a large folder still sees the parts that matter.
    /// </summary>
    private static string BuildDocumentExcerpts(IReadOnlyList<TopicDocument> documents, string question, int maxChars)
    {
        if (documents.Count == 0)
        {
            return string.Empty;
        }

        string[] tokens = TokenizeQuery(question).Select(Stem).Where(t => t.Length >= 3).ToArray();
        var chunks = new List<(int Document, int Order, string Text, int Score)>();
        for (int d = 0; d < documents.Count; d++)
        {
            int order = 0;
            foreach (string chunk in SplitChunks(documents[d].Markdown, 1400))
            {
                int score = tokens.Length == 0 ? 0 : tokens.Count(t => chunk.Contains(t, StringComparison.OrdinalIgnoreCase));
                chunks.Add((d, order++, chunk, score));
            }
        }

        var picked = new HashSet<(int, int)>();
        int budget = maxChars;
        // The opening of every document gives the model a sense of what each one is.
        foreach (var chunk in chunks.Where(c => c.Order == 0))
        {
            if (budget <= 0)
            {
                break;
            }

            string opening = Cap(chunk.Text, 600);
            picked.Add((chunk.Document, chunk.Order));
            budget -= opening.Length;
        }

        foreach (var chunk in chunks.Where(c => c.Score > 0).OrderByDescending(c => c.Score))
        {
            if (budget <= 0)
            {
                break;
            }

            if (picked.Add((chunk.Document, chunk.Order)))
            {
                budget -= chunk.Text.Length;
            }
        }

        var builder = new StringBuilder();
        foreach (var group in chunks.Where(c => picked.Contains((c.Document, c.Order))).GroupBy(c => c.Document))
        {
            builder.Append("### [D").Append(group.Key + 1).Append("] ").AppendLine(documents[group.Key].Title);
            foreach (var chunk in group.OrderBy(c => c.Order))
            {
                builder.AppendLine(chunk.Order == 0 && chunk.Score == 0 ? Cap(chunk.Text, 600) : chunk.Text).AppendLine("…");
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>Russian words change endings a lot; matching on the first letters is enough to find the passage.</summary>
    private static string Stem(string token) => token.Length > 6 ? token[..(token.Length - 2)] : token;

    /// <summary>
    /// Passages of about <paramref name="size"/> characters along paragraph and row boundaries. Every passage starts
    /// with the heading it belongs to, and a passage of a table repeats the table header, so a row found by the
    /// question still says which sheet and which columns it comes from.
    /// </summary>
    private static IEnumerable<string> SplitChunks(string markdown, int size)
    {
        var current = new StringBuilder();
        string heading = string.Empty;

        IEnumerable<string> Flush()
        {
            if (current.Length > 0)
            {
                yield return current.ToString().Trim();
                current.Clear();
            }
        }

        void Start(string prefix)
        {
            if (current.Length == 0 && prefix.Length > 0)
            {
                current.AppendLine(prefix);
            }
        }

        foreach (string block in Regex.Split(markdown.Replace("\r\n", "\n"), @"\n\s*\n"))
        {
            string paragraph = block.Trim();
            if (paragraph.Length == 0)
            {
                continue;
            }

            if (paragraph.StartsWith('#') && !paragraph.Contains('\n'))
            {
                heading = paragraph;
            }

            string[] lines = paragraph.Split('\n');
            bool isTable = lines.Length > 2 && lines[0].TrimStart().StartsWith('|') && lines[1].Contains("---", StringComparison.Ordinal);
            if (current.Length > 0 && current.Length + paragraph.Length > size)
            {
                foreach (string chunk in Flush())
                {
                    yield return chunk;
                }
            }

            if (paragraph.Length <= size)
            {
                Start(paragraph == heading ? string.Empty : heading);
                current.AppendLine(paragraph).AppendLine();
                continue;
            }

            string tableHeader = isTable ? lines[0] + "\n" + lines[1] : string.Empty;
            string prefix = (heading.Length > 0 && paragraph != heading ? heading + "\n" : string.Empty) + tableHeader;
            foreach (string line in lines.Skip(isTable ? 2 : 0))
            {
                if (current.Length > 0 && current.Length + line.Length > size)
                {
                    foreach (string chunk in Flush())
                    {
                        yield return chunk;
                    }
                }

                Start(prefix);
                if (line.Length > size)
                {
                    for (int i = 0; i < line.Length; i += size)
                    {
                        current.AppendLine(line.Substring(i, Math.Min(size, line.Length - i)));
                        foreach (string chunk in Flush())
                        {
                            yield return chunk;
                        }

                        Start(prefix);
                    }

                    continue;
                }

                current.AppendLine(line);
            }

            current.AppendLine();
        }

        foreach (string chunk in Flush())
        {
            yield return chunk;
        }
    }

    private static string Cap(string text, int maxChars) => text.Length > maxChars ? text[..maxChars] + "…" : text;
}
