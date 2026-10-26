using System.Text;
using System.Text.RegularExpressions;

namespace CuePilotDesktop.Services;

/// <summary>
/// What the topic graph adds to a question: the entities it names, their direct links, the names of the linked
/// entities as extra search words, and the sessions and documents those entities come from.
/// </summary>
public sealed class TopicGraphContext
{
    public static readonly TopicGraphContext Empty = new();

    /// <summary>Entities and links in a form the model can read; empty when the question names no entity.</summary>
    public string Text { get; init; } = string.Empty;
    /// <summary>Words of the linked entities that the question itself does not contain.</summary>
    public IReadOnlyList<string> RelatedTerms { get; init; } = [];
    /// <summary>Session titles and document names the found entities come from, normalized.</summary>
    public IReadOnlyList<string> SourceNames { get; init; } = [];
    public int MatchedNodes { get; init; }
    public int LinkedNodes { get; init; }

    public bool IsEmpty => MatchedNodes == 0;

    /// <summary>Whether a session title or a document name is one of <see cref="SourceNames"/>.</summary>
    public bool IsSource(string name)
    {
        string normalized = KnowledgeGraphPruner.Normalize(name);
        return normalized.Length > 0 && SourceNames.Any(source => KnowledgeGraphPruner.Matches(normalized, source) || KnowledgeGraphPruner.Matches(source, normalized));
    }
}

/// <summary>
/// Graph-assisted retrieval for topic questions. The question is matched against the node labels; the matched
/// nodes and their neighbours tell which people, tasks and documents belong to the answer even when the question
/// does not name them («что поручили Ивану» → the tasks linked to Иван and the documents they come from).
/// No model call: this runs on every question.
/// </summary>
public static class TopicGraphRetrieval
{
    private const int MaxMatchedNodes = 8;
    private const int MaxLinkedNodes = 20;
    private const int MaxTextChars = 5000;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "что", "как", "кто", "где", "когда", "какие", "какой", "какая", "какое", "каких", "это", "эти", "этот", "для",
        "или", "при", "про", "под", "над", "без", "был", "была", "были", "было", "есть", "нам", "нас", "вас", "его",
        "она", "они", "все", "всё", "ещё", "еще", "уже", "так", "там", "тут", "чем", "чей", "чья", "чьи", "сейчас",
        "можно", "нужно", "надо", "почему", "зачем", "сколько", "который", "которые", "теме", "тема", "темы",
        "the", "and", "what", "who", "when", "where", "how", "why", "with", "from", "about", "was", "were", "are",
        "has", "have", "this", "that", "which", "topic"
    };

    public static TopicGraphContext Select(TopicGraph? graph, string question, bool english)
    {
        if (graph == null || graph.Nodes.Count == 0 || string.IsNullOrWhiteSpace(question))
        {
            return TopicGraphContext.Empty;
        }

        string[] questionWords = Words(question);
        if (questionWords.Length == 0)
        {
            return TopicGraphContext.Empty;
        }

        // A node matches when the question holds at least half of the words of its label (or two of a long one).
        var matched = new List<(GraphNodeData Node, double Score, int Order)>();
        for (int i = 0; i < graph.Nodes.Count; i++)
        {
            GraphNodeData node = graph.Nodes[i];
            string[] labelWords = Words(node.Label);
            if (labelWords.Length == 0)
            {
                continue;
            }

            int hits = labelWords.Count(word => questionWords.Any(q => SameWord(q, word)));
            double score = hits / (double)labelWords.Length;
            if (hits > 0 && (score >= 0.5 || hits >= 2))
            {
                matched.Add((node, score, i));
            }
        }

        if (matched.Count == 0)
        {
            return TopicGraphContext.Empty;
        }

        // The model lists nodes by importance, so the order breaks ties.
        List<GraphNodeData> seeds = matched
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Order)
            .Take(MaxMatchedNodes)
            .Select(m => m.Node)
            .ToList();
        var seedIds = new HashSet<string>(seeds.Select(n => n.Id));
        Dictionary<string, GraphNodeData> byId = graph.Nodes.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());
        Dictionary<string, int> order = graph.Nodes.Select((n, i) => (n.Id, i)).GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().i);

        List<GraphEdgeData> seedEdges = graph.Edges
            .Where(e => (seedIds.Contains(e.Source) || seedIds.Contains(e.Target)) && byId.ContainsKey(e.Source) && byId.ContainsKey(e.Target))
            .ToList();

        // Neighbours linked to several of the matched nodes come first: «Иван» and «тестирование» both point at the task.
        List<GraphNodeData> linked = seedEdges
            .Select(e => seedIds.Contains(e.Source) ? e.Target : e.Source)
            .Where(id => !seedIds.Contains(id))
            .GroupBy(id => id)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => order[g.Key])
            .Take(MaxLinkedNodes)
            .Select(g => byId[g.Key])
            .ToList();
        var shownIds = new HashSet<string>(seedIds.Concat(linked.Select(n => n.Id)));

        var text = new StringBuilder();
        text.AppendLine(english ? "Entities named in the question:" : "Сущности из вопроса:");
        foreach (GraphNodeData node in seeds)
        {
            AppendNode(text, node, english);
        }

        List<GraphEdgeData> edges = seedEdges.Where(e => shownIds.Contains(e.Source) && shownIds.Contains(e.Target)).ToList();
        if (edges.Count > 0)
        {
            text.AppendLine().AppendLine(english ? "Their links:" : "Их связи:");
            foreach (GraphEdgeData edge in edges)
            {
                if (text.Length > MaxTextChars)
                {
                    break;
                }

                text.Append("- ").Append(byId[edge.Source].Label)
                    .Append(" —").Append(string.IsNullOrWhiteSpace(edge.Label) ? (english ? "related to" : "связан с") : edge.Label.Trim()).Append("→ ")
                    .AppendLine(byId[edge.Target].Label);
            }
        }

        if (linked.Count > 0 && text.Length < MaxTextChars)
        {
            text.AppendLine().AppendLine(english ? "Linked entities:" : "Связанные сущности:");
            foreach (GraphNodeData node in linked)
            {
                if (text.Length > MaxTextChars)
                {
                    break;
                }

                AppendNode(text, node, english);
            }
        }

        var questionSet = new HashSet<string>(questionWords);
        List<string> related = seeds.Concat(linked)
            .SelectMany(n => Words(n.Label))
            .Where(word => !questionSet.Contains(word) && !questionWords.Any(q => SameWord(q, word)))
            .Distinct()
            .ToList();
        List<string> sources = seeds.Concat(linked)
            .SelectMany(n => string.Equals(n.Type, "document", StringComparison.OrdinalIgnoreCase) ? n.Sources.Append(n.Label) : n.Sources)
            .Select(KnowledgeGraphPruner.Normalize)
            .Where(name => name.Length > 0)
            .Distinct()
            .ToList();

        return new TopicGraphContext
        {
            Text = text.ToString().TrimEnd(),
            RelatedTerms = related,
            SourceNames = sources,
            MatchedNodes = seeds.Count,
            LinkedNodes = linked.Count
        };
    }

    /// <summary>Lower-case words of 3+ letters without stop words, trimmed like the document search does.</summary>
    internal static string[] Words(string text) =>
        Regex.Split(text.ToLowerInvariant().Replace('ё', 'е'), @"\W+")
            .Where(word => word.Length >= 3 && !StopWords.Contains(word))
            .Select(word => word.Length > 6 ? word[..^2] : word)
            .Distinct()
            .ToArray();

    /// <summary>
    /// Same word in another form: «ивану» and «иван», «марии» and «мария», «тестированием» and «тестирование»:
    /// a common start of 4+ letters and at most two different letters at the end of each.
    /// </summary>
    internal static bool SameWord(string a, string b)
    {
        if (a == b)
        {
            return true;
        }

        int common = 0;
        while (common < a.Length && common < b.Length && a[common] == b[common])
        {
            common++;
        }

        return common >= 4 && a.Length - common <= 2 && b.Length - common <= 2;
    }

    private static void AppendNode(StringBuilder text, GraphNodeData node, bool english)
    {
        text.Append("- ").Append(node.Label).Append(" (").Append(node.Type).Append(')');
        if (!string.IsNullOrWhiteSpace(node.Description))
        {
            text.Append(": ").Append(node.Description.Trim());
        }

        if (node.Sources.Count > 0)
        {
            text.Append(english ? " [sources: " : " [источники: ").Append(string.Join("; ", node.Sources.Take(4))).Append(']');
        }

        text.AppendLine();
    }
}
