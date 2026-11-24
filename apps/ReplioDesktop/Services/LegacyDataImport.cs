using System.IO;
using System.Text;

namespace ReplioDesktop.Services;

/// <summary>
/// Replio was called CuePilot before. On the first start of Replio the user's CuePilot data (sessions, topics,
/// knowledge base, screenshots, settings with the key) is copied from %AppData%\CuePilot to %AppData%\Replio, and the
/// small files of %LocalAppData%\CuePilot (overlay position) next to Replio's. Nothing is overwritten: a file that
/// already exists in Replio stays as it is, and the CuePilot folders are left untouched. Speech models are not copied
/// (hundreds of MB): SpeechModelCatalog finds them in the old folder. Absolute paths inside the copied JSON and
/// Markdown files are moved over to the new folder. A marker file makes this run once.
/// </summary>
public static class LegacyDataImport
{
    public const string LegacyName = "CuePilot";
    private const string MarkerFileName = "imported-from-cuepilot.txt";

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { "visual-context", "models", "logs" };

    public static string LegacyRoamingRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyName);
    public static string RoamingRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Replio");
    public static string LegacyLocalRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyName);
    public static string LocalRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Replio");

    /// <summary>True when there is CuePilot data that has not been brought over yet (cheap: two directory checks).</summary>
    public static bool IsNeeded() =>
        Directory.Exists(LegacyRoamingRoot) && !File.Exists(Path.Combine(RoamingRoot, MarkerFileName));

    /// <summary>Copies the data. Slow on large libraries: call it off the UI thread. Never throws.</summary>
    public static string Run()
    {
        var report = new StringBuilder();
        try
        {
            if (!IsNeeded())
            {
                return string.Empty;
            }

            int copied = CopyMissing(LegacyRoamingRoot, RoamingRoot, report);
            if (Directory.Exists(LegacyLocalRoot))
            {
                copied += CopyMissing(LegacyLocalRoot, LocalRoot, report);
            }

            Directory.CreateDirectory(RoamingRoot);
            File.WriteAllText(
                Path.Combine(RoamingRoot, MarkerFileName),
                $"Data copied from {LegacyRoamingRoot} on {DateTime.Now:yyyy-MM-dd HH:mm}: {copied} file(s). The CuePilot folder was not changed.{Environment.NewLine}{report}");
            return $"[import] CuePilot data copied to Replio: {copied} file(s).";
        }
        catch (Exception ex)
        {
            return "[import] CuePilot data import failed: " + ex.Message;
        }
    }

    private static int CopyMissing(string sourceRoot, string targetRoot, StringBuilder report)
    {
        int copied = 0;
        var pending = new Stack<string>();
        pending.Push(sourceRoot);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            string relativeDir = Path.GetRelativePath(sourceRoot, directory);
            string targetDir = relativeDir == "." ? targetRoot : Path.Combine(targetRoot, relativeDir);
            try
            {
                Directory.CreateDirectory(targetDir);
                foreach (string sub in Directory.EnumerateDirectories(directory))
                {
                    if (!(directory == sourceRoot && SkippedFolders.Contains(Path.GetFileName(sub))))
                    {
                        pending.Push(sub);
                    }
                }

                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    string target = Path.Combine(targetDir, Path.GetFileName(file));
                    if (File.Exists(target) || Path.GetFileName(file).StartsWith("cuepilot-", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        if (IsTextWithPaths(file))
                        {
                            File.WriteAllText(target, MovePaths(File.ReadAllText(file), sourceRoot, targetRoot), new UTF8Encoding(false));
                        }
                        else
                        {
                            File.Copy(file, target, overwrite: false);
                        }

                        copied++;
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine("not copied: " + file + " (" + ex.Message + ")");
                    }
                }
            }
            catch (Exception ex)
            {
                report.AppendLine("folder skipped: " + directory + " (" + ex.Message + ")");
            }
        }

        return copied;
    }

    private static bool IsTextWithPaths(string file)
    {
        string extension = Path.GetExtension(file);
        return extension.Equals(".json", StringComparison.OrdinalIgnoreCase) || extension.Equals(".md", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Old folder → new folder in plain, JSON-escaped and file:// URI spellings.</summary>
    private static string MovePaths(string text, string oldRoot, string newRoot)
    {
        string result = text.Replace(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase);
        result = result.Replace(oldRoot.Replace("\\", "\\\\"), newRoot.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase);
        try
        {
            result = result.Replace(new Uri(oldRoot).AbsoluteUri, new Uri(newRoot).AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        }
        catch (UriFormatException)
        {
        }

        return result;
    }
}
