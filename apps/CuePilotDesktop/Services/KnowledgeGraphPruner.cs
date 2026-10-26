using System.IO;

namespace CuePilotDesktop.Services;

/// <summary>
/// Removes from a topic graph what only removed sources said, without asking the model again:
/// the document nodes of those sources, entities whose every source was removed, and their links.
/// Entities that other sessions or documents also mention stay, with the removed names dropped from their sources.
/// </summary>
public static class KnowledgeGraphPruner
{
    /// <returns>The number of nodes removed.</returns>
    public static int Prune(TopicGraph graph, IReadOnlyCollection<string> removedNames, IReadOnlyCollection<string> remainingNames)
    {
        List<string> removed = removedNames.Select(Normalize).Where(n => n.Length > 0).Distinct().ToList();
        List<string> remaining = remainingNames.Select(Normalize).Where(n => n.Length > 0).Distinct().ToList();
        if (removed.Count == 0)
        {
            return 0;
        }

        bool IsRemoved(string name)
        {
            string normalized = Normalize(name);
            // Remaining names (other documents, session titles) win: a session called like a removed folder stays.
            return removed.Any(r => Matches(normalized, r)) && !remaining.Any(r => normalized == r);
        }

        var dropped = new HashSet<string>();
        foreach (GraphNodeData node in graph.Nodes)
        {
            if (string.Equals(node.Type, "document", StringComparison.OrdinalIgnoreCase) && IsRemoved(node.Label))
            {
                dropped.Add(node.Id);
                continue;
            }

            if (node.Sources.Count == 0)
            {
                continue;
            }

            List<string> kept = node.Sources.Where(source => !IsRemoved(source)).ToList();
            if (kept.Count == node.Sources.Count)
            {
                continue;
            }

            if (kept.Count == 0)
            {
                dropped.Add(node.Id);
            }
            else
            {
                node.Sources = kept;
            }
        }

        graph.Edges = graph.Edges.Where(e => !dropped.Contains(e.Source) && !dropped.Contains(e.Target)).ToList();
        int before = graph.Nodes.Count;
        graph.Nodes = graph.Nodes.Where(n => !dropped.Contains(n.Id)).ToList();
        return before - graph.Nodes.Count;
    }

    /// <summary>Lower case without the file extension and the «[D2]» reference the model sometimes keeps.</summary>
    internal static string Normalize(string name)
    {
        string text = name.Trim();
        if (text.StartsWith('[') && text.IndexOf(']') is > 0 and < 8 and var close)
        {
            text = text[(close + 1)..].Trim();
        }

        string extension = Path.GetExtension(text);
        if (extension.Length is > 1 and <= 5 && DocumentParser.IsSupported(text))
        {
            text = text[..^extension.Length];
        }

        return text.Trim().ToLowerInvariant();
    }

    internal static bool Matches(string name, string removed) =>
        name == removed || (removed.Length >= 8 && name.Contains(removed, StringComparison.Ordinal));
}
