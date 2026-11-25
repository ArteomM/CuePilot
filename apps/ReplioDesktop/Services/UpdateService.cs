using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace ReplioDesktop.Services;

/// <summary>
/// What replio.ru/download/version.json announces:
/// {"version":"1.1.1","url":"https://replio.ru/download/Replio-Setup-1.1.1.exe","sha256":"…","size":75000000,
///  "notes":"…","mandatory":false,"published":"2026-11-25"}
/// </summary>
public sealed record ReplioUpdateInfo(Version Version, string Url, string Sha256, long Size, string Notes, bool Mandatory)
{
    public string VersionText => Version.ToString(3);

    public static ReplioUpdateInfo? Parse(string json, Uri baseUri)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string? versionText = Json.Text(root, "version");
        string? url = Json.Text(root, "url");
        if (string.IsNullOrWhiteSpace(versionText) || string.IsNullOrWhiteSpace(url) || !Version.TryParse(versionText.Trim(), out Version? version))
        {
            return null;
        }

        // A relative link («Replio-Setup.exe») is read next to version.json.
        if (!Uri.TryCreate(baseUri, url.Trim(), out Uri? absolute) || (absolute.Scheme != Uri.UriSchemeHttps && absolute.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }

        long size = root.TryGetProperty("size", out JsonElement sizeElement) && sizeElement.ValueKind == JsonValueKind.Number && sizeElement.TryGetInt64(out long bytes) ? bytes : 0;
        return new ReplioUpdateInfo(
            Normalize(version),
            absolute.ToString(),
            (Json.Text(root, "sha256") ?? string.Empty).Trim().ToLowerInvariant(),
            size,
            (Json.Text(root, "notes") ?? string.Empty).Trim(),
            Json.Bool(root, "mandatory"));
    }

    public static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0));
}

/// <summary>
/// Checks replio.ru for a newer Windows installer, downloads it into %LocalAppData%\Replio\updates, checks its
/// SHA-256 and starts it with «--update». The installer waits for the app to close, installs over it without
/// questions and starts the new version; %AppData%\Replio (settings, key, sessions) is never touched.
/// REPLIO_UPDATE_URL points the check at another version.json (tests, a local server).
/// </summary>
public static class UpdateService
{
    public const string DefaultFeedUrl = "https://replio.ru/download/version.json";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    /// <summary>Self-update exists for the Windows installer only; the Mac build is updated from its .dmg.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    public static Uri FeedUrl
    {
        get
        {
            string? custom = Environment.GetEnvironmentVariable("REPLIO_UPDATE_URL");
            return Uri.TryCreate(string.IsNullOrWhiteSpace(custom) ? DefaultFeedUrl : custom.Trim(), UriKind.Absolute, out Uri? uri)
                ? uri
                : new Uri(DefaultFeedUrl);
        }
    }

    public static Version CurrentVersion =>
        ReplioUpdateInfo.Normalize(typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0));

    public static string UpdatesDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Replio", "updates");

    /// <summary>The announced version when it is newer than this one, otherwise null.</summary>
    public static async Task<ReplioUpdateInfo?> CheckAsync(string deviceId, CancellationToken cancellationToken)
    {
        Uri feed = FeedUrl;
        using var request = new HttpRequestMessage(HttpMethod.Get, feed);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        foreach ((string name, string value) in ReplioApi.Headers(deviceId, null))
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using HttpResponseMessage response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        ReplioUpdateInfo? info = ReplioUpdateInfo.Parse(json, feed);
        return info != null && info.Version > CurrentVersion ? info : null;
    }

    /// <summary>Downloads the installer (progress 0–100) and returns its path; a wrong checksum deletes it and throws.</summary>
    public static async Task<string> DownloadAsync(ReplioUpdateInfo info, IProgress<int> progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(UpdatesDirectory);
        foreach (string old in Directory.EnumerateFiles(UpdatesDirectory, "Replio-Setup-*.exe"))
        {
            TryDelete(old);
        }

        string target = Path.Combine(UpdatesDirectory, $"Replio-Setup-{info.VersionText}.exe");
        string partial = target + ".part";
        using (HttpResponseMessage response = await Http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? info.Size;
            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                int lastPercent = -1;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    done += read;
                    int percent = total > 0 ? (int)Math.Min(100, done * 100 / total) : 0;
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress.Report(percent);
                    }
                }
            }
        }

        if (info.Sha256.Length > 0)
        {
            string actual;
            await using (var file = File.OpenRead(partial))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            }

            if (!string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(partial);
                throw new InvalidDataException("Контрольная сумма установщика не совпала. Файл удалён, попробуйте ещё раз.");
            }
        }

        File.Move(partial, target, overwrite: true);
        progress.Report(100);
        return target;
    }

    /// <summary>Starts the installer in update mode; the caller then closes the app.</summary>
    public static void StartInstaller(string installerPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "--update",
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? UpdatesDirectory,
            UseShellExecute = true
        });
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // A locked leftover is harmless: the next download has its own name.
        }
    }
}
