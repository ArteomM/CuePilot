using System.IO;
using System.Text.Json;
using ReplioDesktop.Models;

namespace ReplioDesktop.Services;

/// <summary>
/// Topics live in &lt;data folder&gt;\topics.json (see DataLocation) next to the sessions folder; sessions point to a topic by TopicId.
/// </summary>
public class JsonTopicStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;

    public JsonTopicStore()
    {
        string root = DataLocation.Root;
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "topics.json");
    }

    public List<SessionTopic> Load()
    {
        // A cloud client leaves «topics (2).json»-style copies when two computers saved at once: fold them in.
        DataConflicts.MergeTopicCopies(_path);
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            using FileStream stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize<List<SessionTopic>>(stream, JsonOptions) ?? [];
        }
        catch
        {
            // A broken file must not take the history down; keep a copy and start empty.
            try
            {
                File.Copy(_path, _path + ".broken", overwrite: true);
            }
            catch
            {
            }

            return [];
        }
    }

    public void Save(IEnumerable<SessionTopic> topics)
    {
        string tempPath = _path + ".tmp";
        File.WriteAllBytes(tempPath, JsonSerializer.SerializeToUtf8Bytes(topics.ToList(), JsonOptions));
        File.Move(tempPath, _path, overwrite: true);
    }
}
