using System.IO;
using System.Text;
using System.Text.Json;
using CuePilotDesktop.Models;

namespace CuePilotDesktop.Services;

/// <summary>
/// Parsed Markdown of the knowledge sources and the entity graph of every topic:
/// %AppData%\CuePilot\knowledge\&lt;topicId&gt;\&lt;sourceId&gt;.md and graph.json.
/// </summary>
public class KnowledgeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public KnowledgeStore()
    {
        Root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CuePilot", "knowledge");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string GetTopicFolder(string topicId)
    {
        string folder = System.IO.Path.Combine(Root, topicId);
        Directory.CreateDirectory(folder);
        return folder;
    }

    public string GetSourcePath(string topicId, string sourceId) => System.IO.Path.Combine(GetTopicFolder(topicId), sourceId + ".md");

    public string ReadSource(string topicId, string sourceId)
    {
        string path = GetSourcePath(topicId, sourceId);
        return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
    }

    public void WriteSource(string topicId, string sourceId, string markdown)
    {
        string path = GetSourcePath(topicId, sourceId);
        string tempPath = path + ".tmp";
        File.WriteAllText(tempPath, markdown, new UTF8Encoding(false));
        File.Move(tempPath, path, overwrite: true);
    }

    public void DeleteSource(string topicId, string sourceId)
    {
        string path = GetSourcePath(topicId, sourceId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void DeleteTopic(string topicId)
    {
        string folder = System.IO.Path.Combine(Root, topicId);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    public TopicGraph? ReadGraph(string topicId)
    {
        string path = System.IO.Path.Combine(GetTopicFolder(topicId), "graph.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TopicGraph>(File.ReadAllText(path, Encoding.UTF8), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public void WriteGraph(string topicId, TopicGraph graph)
    {
        string path = System.IO.Path.Combine(GetTopicFolder(topicId), "graph.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(graph, JsonOptions), new UTF8Encoding(false));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>Stable across runs, unlike string.GetHashCode.</summary>
    public static string Hash(string text)
    {
        byte[] digest = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(digest, 0, 8);
    }

    /// <summary>Size and write time of a file, or of every supported file in a folder; changes when the original changes.</summary>
    public static string ComputeStamp(KnowledgeSource source)
    {
        try
        {
            switch (source.Kind)
            {
                case KnowledgeSourceKind.File:
                    var file = new FileInfo(source.Path);
                    return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "missing";
                case KnowledgeSourceKind.Folder:
                    if (!Directory.Exists(source.Path))
                    {
                        return "missing";
                    }

                    var builder = new StringBuilder();
                    int count = 0;
                    foreach (string path in DocumentParser.EnumerateFolder(source.Path))
                    {
                        var info = new FileInfo(path);
                        builder.Append(path.ToLowerInvariant()).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
                        count++;
                    }

                    return $"{count}:{Hash(builder.ToString())}";
                default:
                    return "note:" + Hash(source.Title + "\n" + source.NoteText);
            }
        }
        catch (Exception)
        {
            return "error";
        }
    }
}

/// <summary>Entities and relations extracted from a topic, drawn on the «Граф» tab.</summary>
public class TopicGraph
{
    public List<GraphNodeData> Nodes { get; set; } = [];
    public List<GraphEdgeData> Edges { get; set; } = [];
}

public class GraphNodeData
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    /// <summary>person, organization, project, task, decision, document, term, date, place, session.</summary>
    public string Type { get; set; } = "term";
    public string Description { get; set; } = string.Empty;
    /// <summary>Where the entity comes from: session titles and document names.</summary>
    public List<string> Sources { get; set; } = [];
    /// <summary>Saved position from the last layout, so the picture stays stable between visits.</summary>
    public double? X { get; set; }
    public double? Y { get; set; }
}

public class GraphEdgeData
{
    public string Source { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}
