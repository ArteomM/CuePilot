using System.IO;
using System.Text.Json;
using CuePilotDesktop.Models;

namespace CuePilotDesktop.Services;

/// <summary>
/// Topics live in %AppData%\CuePilot\topics.json next to the sessions folder; sessions point to a topic by TopicId.
/// </summary>
public class JsonTopicStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;

    public JsonTopicStore()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CuePilot");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "topics.json");
    }

    public List<SessionTopic> Load()
    {
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
