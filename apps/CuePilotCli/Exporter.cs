using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CuePilotDesktop.Models;

namespace CuePilotCli;

/// <summary>
/// A session as a folder an agent can read and pass on: session.md (everything), summary.md, transcript.md,
/// screenshots/ with the pictures, and session.json as the app saved it. Links inside point to the copies.
/// </summary>
public static class Exporter
{
    public sealed record Result(string Folder, IReadOnlyList<string> Files);

    public static Result Export(Archive archive, SessionSummary session, string? outRoot)
    {
        string root = string.IsNullOrWhiteSpace(outRoot) ? archive.ExportsFolder : Path.GetFullPath(outRoot);
        string folder = Path.Combine(root, FolderName(session));
        Directory.CreateDirectory(folder);
        var files = new List<string>();

        // Screenshots first: the Markdown links to the copies that exist.
        var copies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in session.Screenshots.Select(s => s.FilePath)
                     .Concat(session.Transcript.Select(t => t.ScreenshotPath ?? string.Empty))
                     .Where(p => p.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            string shots = Path.Combine(folder, "screenshots");
            Directory.CreateDirectory(shots);
            string target = Path.Combine(shots, Path.GetFileName(path));
            File.Copy(path, target, overwrite: true);
            copies[path] = "screenshots/" + Uri.EscapeDataString(Path.GetFileName(path));
            files.Add(target);
        }

        string Link(string path) => copies.TryGetValue(path, out string? relative) ? relative : new Uri(Path.GetFullPath(path)).AbsoluteUri;

        files.Add(Write(folder, "session.md", Render.SessionMarkdown(archive, session, withTranscript: true, Link)));
        files.Add(Write(folder, "summary.md", Render.SummaryMarkdown(session, Link)));
        files.Add(Write(folder, "transcript.md", Render.TranscriptMarkdown(session, Link)));

        string json = Path.Combine(folder, "session.json");
        using (var source = new FileStream(archive.SessionFile(session), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (FileStream target = File.Create(json))
        {
            source.CopyTo(target);
        }

        files.Add(json);
        return new Result(folder, files);
    }

    /// <summary>«2026-11-17 1002 KEGOC. Разбор DEMO5» without characters a file system refuses.</summary>
    private static string FolderName(SessionSummary session)
    {
        string title = Regex.Replace(session.Title, @"[\\/:*?""<>|\x00-\x1F]+", " ");
        title = Regex.Replace(title, @"\s{2,}", " ").Trim().TrimEnd('.', ' ');
        if (title.Length > 60)
        {
            title = title[..60].TrimEnd('.', ' ');
        }

        string stamp = session.StartedAt.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture);
        return title.Length == 0 ? stamp + " " + session.Id[..8] : stamp + " " + title;
    }

    private static string Write(string folder, string name, string text)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }
}
