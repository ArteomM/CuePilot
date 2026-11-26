using System.IO;
using System.Text.RegularExpressions;

namespace ReplioDesktop.Services;

/// <summary>
/// Where Replio keeps the user's data: sessions, topics, knowledge base and screenshots.
/// By default the «.replio» folder in the user's home (C:\Users\name\.replio, /Users/name/.replio), the way Claude keeps
/// its data in «.claude»: easy to find for the user and for agents. Data from before 1.1.4 stays where it was
/// (%AppData%\Replio, ~/Library/Application Support/Replio) until the user moves it in Settings; nothing is moved
/// at start. The user can pick another folder in Settings, for example inside Яндекс Диск,
/// OneDrive or iCloud Drive, and the cloud client then syncs the data between computers; nothing goes to Replio's
/// servers. Settings, the key, secrets, logs and speech models always stay in %AppData% / %LocalAppData% on this
/// computer: they are per device and must never be overwritten by another device.
/// The chosen folder is written to %AppData%\Replio\data-folder.txt (read before anything else starts).
/// REPLIO_DATA overrides both (tests, agents).
/// </summary>
public static class DataLocation
{
    public const string PointerFileName = "data-folder.txt";

    /// <summary>Data that moves with the folder; everything else stays per device.</summary>
    public static readonly string[] DataEntries = ["sessions", "topics.json", "knowledge", "visual-context", "exports"];

    // «C:\Users\x\AppData\Roaming\Replio\visual-context», «/Users/x/Library/…/Replio/visual-context»: any root.
    private static readonly Regex ForeignVisualRoot = new(
        @"^.*?[\\/]visual-context(?=[\\/])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Summary links are file URIs: «file:///C:/Users/x/…/visual-context/…».
    private static readonly Regex ForeignVisualUri = new(
        @"file:///[^)\s]*?/visual-context/",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string? _root;

    /// <summary>%AppData%\Replio: settings, key and other per-device files.</summary>
    public static string ConfigRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Replio");

    /// <summary>~/.replio: the data folder of a new installation.</summary>
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".replio");

    /// <summary>
    /// Before 1.1.4 the data lived next to the settings. Such data keeps being used (it is never moved silently)
    /// until the user moves it to <see cref="DefaultRoot"/> in Settings; a new installation starts there.
    /// </summary>
    public static string LegacyRoot => ConfigRoot;

    private static bool HasData(string folder)
    {
        try
        {
            return Directory.Exists(Path.Combine(folder, "sessions")) || File.Exists(Path.Combine(folder, "topics.json")) ||
                   File.Exists(Path.Combine(folder, "sessions.json"));
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static string PointerPath => Path.Combine(ConfigRoot, PointerFileName);

    /// <summary>The data folder in use for this run.</summary>
    public static string Root => _root ??= ResolveRoot();

    public static bool IsCustom => !SamePath(Root, DefaultRoot);

    /// <summary>The folder chosen in Settings, or empty for the default.</summary>
    public static string ChosenFolder
    {
        get
        {
            try
            {
                return File.Exists(PointerPath) ? File.ReadAllText(PointerPath).Trim() : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }

    private static string ResolveRoot()
    {
        string? env = Environment.GetEnvironmentVariable("REPLIO_DATA");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env.Trim();
        }

        string chosen = ChosenFolder;
        if (chosen.Length > 0)
        {
            try
            {
                // A cloud folder that is not mounted yet (drive letter missing) must not silently start an empty
                // history in a new place: fall back to the default and let Settings say so.
                if (Directory.Exists(chosen) || Directory.Exists(Path.GetDirectoryName(chosen.TrimEnd('\\', '/')) ?? string.Empty))
                {
                    Directory.CreateDirectory(chosen);
                    return chosen;
                }
            }
            catch (Exception)
            {
            }

            UnavailableChosenFolder = chosen;
        }

        // An existing installation keeps its data where it was, unless the new default already has data.
        if (!HasData(DefaultRoot) && HasData(LegacyRoot))
        {
            return LegacyRoot;
        }

        return DefaultRoot;
    }

    /// <summary>Set when the chosen folder could not be opened at start; the app then used the default.</summary>
    public static string UnavailableChosenFolder { get; private set; } = string.Empty;

    /// <summary>Remembers the folder for the next start (empty = default). The running app keeps its current folder.</summary>
    public static void SaveChoice(string folder)
    {
        Directory.CreateDirectory(ConfigRoot);
        if (string.IsNullOrWhiteSpace(folder) || SamePath(folder, DefaultRoot))
        {
            if (File.Exists(PointerPath))
            {
                File.Delete(PointerPath);
            }

            return;
        }

        File.WriteAllText(PointerPath, Path.GetFullPath(folder.Trim()));
    }

    public static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd('\\', '/'),
                Path.GetFullPath(b).TrimEnd('\\', '/'),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>True when <paramref name="folder"/> is the data folder itself or inside it (or the other way round).</summary>
    public static bool Overlaps(string folder, string other)
    {
        string a = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        string b = Path.GetFullPath(other).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return a.StartsWith(b, comparison) || b.StartsWith(a, comparison);
    }

    /// <summary>
    /// A screenshot path written on another computer (or before the folder moved) points at that computer's
    /// …\visual-context\…; the same file is here under this data folder.
    /// </summary>
    public static string Rebase(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path ?? string.Empty;
        }

        string visualRoot = Path.Combine(Root, "visual-context");
        Match match = ForeignVisualRoot.Match(path);
        if (!match.Success)
        {
            return path;
        }

        string rest = path[match.Length..].TrimStart('\\', '/');
        string local = Path.Combine(visualRoot, rest.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
        return SamePath(local, path) ? path : local;
    }

    /// <summary>Same for paths inside text (Markdown links in a summary).</summary>
    public static string RebaseText(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf("visual-context", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return text ?? string.Empty;
        }

        string visualUri = new Uri(Path.Combine(Path.GetFullPath(Root), "visual-context") + Path.DirectorySeparatorChar).AbsoluteUri;
        return ForeignVisualUri.Replace(text, _ => visualUri);
    }
}
