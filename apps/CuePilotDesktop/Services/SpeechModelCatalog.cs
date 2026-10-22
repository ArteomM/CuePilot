using System.IO;
using System.Net.Http;

namespace CuePilotDesktop.Services;

/// <summary>A whisper.cpp model that can be downloaded from Settings after installation.</summary>
public sealed class SpeechModelInfo
{
    public required string Id { get; init; }
    public required string FileName { get; init; }
    public required string Title { get; init; }
    public required int ApproxSizeMb { get; init; }
    public required int MinCores { get; init; }
    public required int MinRamGb { get; init; }
    public bool Recommended { get; init; }

    public string Url => "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + FileName;

    /// <summary>Resource key of the description (Russian quality, speed, CPU load).</summary>
    public string DescriptionKey => "SpeechModelDesc_" + Id.Replace('-', '_');
}

/// <summary>
/// Speech models are not packed into the installer: the user downloads one from Settings.
/// Downloaded models live in %LocalAppData%\CuePilot\models, which reinstalls and updates never touch.
/// </summary>
public static class SpeechModelCatalog
{
    public const string DefaultFileName = "ggml-small-q8_0.bin";

    private static readonly HttpClient DownloadClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static IReadOnlyList<SpeechModelInfo> Models { get; } = new[]
    {
        new SpeechModelInfo { Id = "small-q8_0", FileName = DefaultFileName, Title = "small (q8_0)", ApproxSizeMb = 265, MinCores = 4, MinRamGb = 4, Recommended = true },
        new SpeechModelInfo { Id = "large-v3-turbo-q5_0", FileName = "ggml-large-v3-turbo-q5_0.bin", Title = "large-v3-turbo (q5_0)", ApproxSizeMb = 575, MinCores = 8, MinRamGb = 8 },
        new SpeechModelInfo { Id = "medium-q5_0", FileName = "ggml-medium-q5_0.bin", Title = "medium (q5_0)", ApproxSizeMb = 540, MinCores = 8, MinRamGb = 8 },
        new SpeechModelInfo { Id = "base-q8_0", FileName = "ggml-base-q8_0.bin", Title = "base (q8_0)", ApproxSizeMb = 80, MinCores = 2, MinRamGb = 2 }
    };

    public static string UserModelsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CuePilot",
        "models");

    public static SpeechModelInfo? Find(string? fileName) =>
        Models.FirstOrDefault(m => string.Equals(m.FileName, fileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The downloaded copy first, then a model next to the app (source checkout or an older installer).</summary>
    public static string? FindInstalledPath(string fileName, string projectRoot)
    {
        string userPath = Path.Combine(UserModelsDirectory, fileName);
        if (File.Exists(userPath))
        {
            return userPath;
        }

        string appPath = Path.Combine(projectRoot, "models", fileName);
        return File.Exists(appPath) ? appPath : null;
    }

    public static bool IsUserDownloaded(string fileName) => File.Exists(Path.Combine(UserModelsDirectory, fileName));

    /// <summary>
    /// Model used for local recognition: an explicit WhisperModelPath, then the model chosen in Settings,
    /// then any installed catalog model (in catalog order), then the legacy ggml-base.bin.
    /// </summary>
    public static string? ResolveActiveModelPath(AppSettings settings, string projectRoot)
    {
        if (!string.IsNullOrWhiteSpace(settings.WhisperModelPath))
        {
            return File.Exists(settings.WhisperModelPath) ? settings.WhisperModelPath : null;
        }

        IEnumerable<string> candidates = new[] { settings.SpeechModelFileName }
            .Concat(Models.Select(m => m.FileName))
            .Append("ggml-base.bin")
            .Where(name => !string.IsNullOrWhiteSpace(name));
        foreach (string fileName in candidates)
        {
            string? path = FindInstalledPath(fileName, projectRoot);
            if (path != null)
            {
                return path;
            }
        }

        return null;
    }

    public static int CpuCores => Environment.ProcessorCount;

    public static double TotalRamGb => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024);

    public static bool SuitsThisPc(SpeechModelInfo model) => CpuCores >= model.MinCores && TotalRamGb >= model.MinRamGb - 0.5;

    /// <summary>
    /// Downloads into a .part file and renames it only after the size matches Content-Length,
    /// so an interrupted download never looks like an installed model.
    /// </summary>
    public static async Task DownloadAsync(SpeechModelInfo model, IProgress<(long Received, long? Total)> progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(UserModelsDirectory);
        string targetPath = Path.Combine(UserModelsDirectory, model.FileName);
        string partPath = targetPath + ".part";

        try
        {
            using HttpResponseMessage response = await DownloadClient.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;

            long needed = (total ?? model.ApproxSizeMb * 1024L * 1024L) + 50L * 1024 * 1024;
            var drive = new DriveInfo(Path.GetPathRoot(UserModelsDirectory)!);
            if (drive.AvailableFreeSpace < needed)
            {
                throw new IOException(string.Format(
                    "Not enough disk space on {0}: {1:0} MB needed, {2:0} MB free.",
                    drive.Name,
                    needed / (1024.0 * 1024),
                    drive.AvailableFreeSpace / (1024.0 * 1024)));
            }

            long received = 0;
            await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                int read;
                long lastReport = 0;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;
                    if (received - lastReport >= 1024 * 1024)
                    {
                        lastReport = received;
                        progress.Report((received, total));
                    }
                }
            }

            if ((total.HasValue && received != total.Value) || received < 10L * 1024 * 1024)
            {
                throw new IOException("Download is incomplete: " + received + " bytes received.");
            }

            progress.Report((received, total));
            File.Move(partPath, targetPath, overwrite: true);
        }
        finally
        {
            TryDelete(partPath);
        }
    }

    public static void DeleteDownloaded(string fileName) => File.Delete(Path.Combine(UserModelsDirectory, fileName));

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
