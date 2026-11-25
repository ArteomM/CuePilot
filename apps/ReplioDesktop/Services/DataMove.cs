using System.IO;
using System.Text.Json;
using ReplioDesktop.Models;

namespace ReplioDesktop.Services;

/// <summary>
/// Brings this computer's data into another data folder (Settings → «Папка данных»). The target may already hold
/// data from another computer (the same cloud folder): nothing there is overwritten. New files are copied, topics
/// are merged, and a session that differs on the two sides is copied as a second version for the user to choose.
/// The old folder is left as it was. Settings, the key and other per-device files never move.
/// </summary>
public static class DataMove
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Why the folder cannot be used, or null.</summary>
    public static string? Validate(string target, string currentRoot)
    {
        if (string.IsNullOrWhiteSpace(target) || !Path.IsPathRooted(target))
        {
            return "path";
        }

        if (DataLocation.SamePath(target, currentRoot))
        {
            return "same";
        }

        // Inside the current data (or the other way round) would copy the data into itself.
        if (DataLocation.Overlaps(target, currentRoot) && !DataLocation.SamePath(target, DataLocation.DefaultRoot))
        {
            return "nested";
        }

        return null;
    }

    /// <summary>Copies; returns (files copied, sessions kept as a second version). Progress is 0–100.</summary>
    public static (int Copied, int Versions) Copy(string source, string target, IProgress<int>? progress)
    {
        Directory.CreateDirectory(target);
        var files = new List<(string From, string Rel)>();
        foreach (string entry in DataLocation.DataEntries)
        {
            string path = Path.Combine(source, entry);
            if (File.Exists(path))
            {
                files.Add((path, entry));
            }
            else if (Directory.Exists(path))
            {
                files.AddRange(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    .Select(f => (f, Path.GetRelativePath(source, f))));
            }
        }

        int copied = 0, versions = 0, done = 0;
        foreach ((string from, string rel) in files)
        {
            string to = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (string.Equals(rel, "topics.json", StringComparison.OrdinalIgnoreCase))
            {
                MergeTopicsInto(from, to);
                copied++;
            }
            else if (!File.Exists(to))
            {
                File.Copy(from, to);
                copied++;
            }
            else if (IsSessionFile(rel) && !SameBytes(from, to))
            {
                // The same meeting changed on both computers: keep ours as a second version (Settings asks).
                string second = Path.Combine(Path.GetDirectoryName(to)!, Path.GetFileNameWithoutExtension(to) + " (" + Environment.MachineName + ").json");
                File.Copy(from, second, overwrite: true);
                versions++;
            }

            done++;
            progress?.Report(files.Count == 0 ? 100 : done * 100 / files.Count);
        }

        progress?.Report(100);
        return (copied, versions);
    }

    private static bool IsSessionFile(string rel) =>
        rel.StartsWith("sessions" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
        rel.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    private static bool SameBytes(string a, string b)
    {
        var infoA = new FileInfo(a);
        var infoB = new FileInfo(b);
        return infoA.Length == infoB.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }

    private static void MergeTopicsInto(string from, string to)
    {
        List<SessionTopic> ours = Read(from) ?? [];
        List<SessionTopic>? theirs = File.Exists(to) ? Read(to) : null;
        if (theirs == null && File.Exists(to))
        {
            // Unreadable target: keep it aside rather than lose it.
            File.Copy(to, to + ".broken", overwrite: true);
        }

        List<SessionTopic> merged = theirs == null ? ours : DataConflicts.MergeTopics(theirs, ours);
        string temp = to + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(merged, WriteOptions));
        File.Move(temp, to, overwrite: true);
    }

    private static List<SessionTopic>? Read(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<List<SessionTopic>>(stream, ReadOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
