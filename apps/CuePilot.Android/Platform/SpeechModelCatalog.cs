using System.IO;
using System.Net.Http;

namespace CuePilotDesktop.Services;

/// <summary>A whisper.cpp model that can be downloaded from Settings (phone version of the catalog).</summary>
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

    /// <summary>Resource key of the desktop description; the phone shows <see cref="PhoneDescription"/>.</summary>
    public string DescriptionKey => "SpeechModelDesc_" + Id.Replace('-', '_');

    /// <summary>What the model is like on a phone: speed of live recording, quality, battery.</summary>
    public string PhoneDescription => CuePilot.Android.Platform.MobileText.Get("PhoneSpeechDesc_" + Id.Replace('-', '_'));

    public string PhoneSuitability => SpeechModelCatalog.SuitsThisPc(this)
        ? CuePilot.Android.Platform.MobileText.Get("PhoneSpeechSuits")
        : string.Format(CuePilot.Android.Platform.MobileText.Get("PhoneSpeechMaySlow"), MinCores, MinRamGb);
}

/// <summary>
/// Same catalog as on the computer (Services/SpeechModelCatalog.cs) with phone recommendations: base and small
/// keep up with live speech on most phones, large-v3-turbo is for recordings in the knowledge base.
/// Models are downloaded into the app's own storage, which updates of the APK keep.
/// </summary>
public static class SpeechModelCatalog
{
    public const string DefaultFileName = "ggml-small-q8_0.bin";

    private static readonly HttpClient DownloadClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static IReadOnlyList<SpeechModelInfo> Models { get; } = new[]
    {
        new SpeechModelInfo { Id = "small-q8_0", FileName = DefaultFileName, Title = "small (q8_0)", ApproxSizeMb = 265, MinCores = 8, MinRamGb = 6, Recommended = true },
        new SpeechModelInfo { Id = "base-q8_0", FileName = "ggml-base-q8_0.bin", Title = "base (q8_0)", ApproxSizeMb = 80, MinCores = 4, MinRamGb = 3 },
        new SpeechModelInfo { Id = "large-v3-turbo-q5_0", FileName = "ggml-large-v3-turbo-q5_0.bin", Title = "large-v3-turbo (q5_0)", ApproxSizeMb = 575, MinCores = 8, MinRamGb = 8 }
    };

    public static string UserModelsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CuePilot",
        "models");

    public static SpeechModelInfo? Find(string? fileName) =>
        Models.FirstOrDefault(m => string.Equals(m.FileName, fileName, StringComparison.OrdinalIgnoreCase));

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

    /// <summary>The model chosen in Settings, then any downloaded catalog model (in catalog order).</summary>
    public static string? ResolveActiveModelPath(AppSettings settings, string projectRoot)
    {
        if (!string.IsNullOrWhiteSpace(settings.WhisperModelPath))
        {
            return File.Exists(settings.WhisperModelPath) ? settings.WhisperModelPath : null;
        }

        IEnumerable<string> candidates = new[] { settings.SpeechModelFileName }
            .Concat(Models.Select(m => m.FileName))
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

    /// <summary>Memory of the phone from /proc/meminfo (the GC figure is the app's limit, not the phone's RAM).</summary>
    public static double TotalRamGb
    {
        get
        {
            try
            {
                foreach (string line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:", StringComparison.Ordinal) &&
                        long.TryParse(new string(line.Where(char.IsDigit).ToArray()), out long kb))
                    {
                        return kb / (1024.0 * 1024);
                    }
                }
            }
            catch (Exception)
            {
            }

            return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024);
        }
    }

    public static bool SuitsThisPc(SpeechModelInfo model) => CpuCores >= model.MinCores && TotalRamGb >= model.MinRamGb - 0.5;

    /// <summary>
    /// Downloads into a .part file and renames it only after the size matches Content-Length, so an interrupted
    /// download never looks like an installed model. Free space is checked on the app's own storage.
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
            // On Android "/" is the system partition; the space that matters is that of the models folder.
            var drive = new DriveInfo(UserModelsDirectory);
            if (drive.AvailableFreeSpace < needed)
            {
                throw new IOException(string.Format(
                    CuePilot.Android.Platform.MobileText.Get("PhoneSpeechNoSpace"),
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
