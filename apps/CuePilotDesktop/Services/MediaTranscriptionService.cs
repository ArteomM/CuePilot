using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CuePilotDesktop.Services;

/// <summary>A recognized piece of a media file, with its place in the file.</summary>
public sealed record MediaSegment(TimeSpan Start, TimeSpan End, string Text);

/// <summary>How far the transcription is; <see cref="Total"/> is zero while the length of the file is unknown.</summary>
public sealed record MediaTranscriptionProgress(TimeSpan Done, TimeSpan Total);

public sealed class MediaTranscriptionRequest
{
    public string MediaPath { get; init; } = string.Empty;
    /// <summary>Folder of DesktopCapture (installed copy) or of its project (source checkout).</summary>
    public string WorkingDirectory { get; init; } = string.Empty;
    public string WhisperBin { get; init; } = string.Empty;
    public string WhisperModel { get; init; } = string.Empty;
    public string SttDir { get; init; } = string.Empty;
    public string Language { get; init; } = "ru";
    public double SilenceThreshold { get; init; } = 0.004;
    public string SttGlossary { get; init; } = string.Empty;
    public bool UseApiStt { get; init; }
    public string SttApiUrl { get; init; } = string.Empty;
    public string SttApiModel { get; init; } = string.Empty;
    public string SttApiKey { get; init; } = string.Empty;
}

public sealed class MediaTranscriptionResult
{
    public List<MediaSegment> Segments { get; } = [];
    public TimeSpan Duration { get; set; }
    public int FailedChunks { get; set; }
    public string LastError { get; set; } = string.Empty;
    public double CostRub { get; set; }
}

/// <summary>
/// Transcribes the sound of an audio or video file for the knowledge base. The work is done by DesktopCapture
/// (<c>--transcribe-file</c>) with the same engine as live capture: local whisper or the STT API, so the
/// recognition settings apply as they are. The process runs in the background; this class only reads its lines.
/// </summary>
public static class MediaTranscriptionService
{
    private const string SttApiKeyEnvironmentVariable = "CUEPILOT_STT_API_KEY";

    public static readonly string[] Extensions =
    [
        ".mp3", ".wav", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".flac", ".wma", ".amr", ".aiff", ".aif", ".caf",
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".3gp", ".mpg", ".mpeg", ".mts", ".m2ts"
    ];

    private static readonly Regex SegmentLine = new(@"^\[file-stt\]\s+(?<start>[\d.]+)\s+(?<end>[\d.]+)\s?(?<text>.*)$", RegexOptions.Compiled);
    private static readonly Regex ProgressLine = new(@"^\[file-progress\]\s+(?<done>[\d.]+)\s+(?<total>[\d.]+)", RegexOptions.Compiled);
    private static readonly Regex StartLine = new(@"^\[file-start\]\s+duration=(?<total>[\d.]+)", RegexOptions.Compiled);
    private static readonly Regex DoneLine = new(@"^\[file-done\].*cost=(?<cost>[\d.]+)", RegexOptions.Compiled);

    /// <summary>"*.mp3;*.wav;…" for a file dialog.</summary>
    public static string FileDialogPatterns => string.Join(";", Extensions.Select(e => "*" + e));

    public static bool IsMedia(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Runs the transcription to the end. Cancelling asks DesktopCapture to stop after the current piece and then
    /// throws <see cref="OperationCanceledException"/>; a file without readable sound throws with the reason.
    /// </summary>
    public static async Task<MediaTranscriptionResult> TranscribeAsync(
        MediaTranscriptionRequest request,
        IProgress<MediaTranscriptionProgress>? progress,
        Action<string>? log,
        CancellationToken token)
    {
        string? executable = CaptureOrchestratorService.FindCaptureExecutable(request.WorkingDirectory);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable ?? "dotnet",
                WorkingDirectory = request.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            }
        };
        if (executable == null)
        {
            foreach (string part in PlatformPaths.CaptureDotnetRunPrefix.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                process.StartInfo.ArgumentList.Add(part);
            }
        }

        foreach (string argument in BuildArguments(request))
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        if (request.UseApiStt)
        {
            // Passed through the environment so the key never shows up in the process command line.
            process.StartInfo.Environment[SttApiKeyEnvironmentVariable] = request.SttApiKey;
        }

        var result = new MediaTranscriptionResult();
        string fileError = string.Empty;
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data) && stderr.Length < 4000)
            {
                lock (stderr)
                {
                    stderr.AppendLine(e.Data);
                }
            }
        };

        using (process)
        {
            process.Start();
            process.BeginErrorReadLine();
            log?.Invoke($"[media] Transcribing {Path.GetFileName(request.MediaPath)} ({(request.UseApiStt ? "API " + request.SttApiModel : "local whisper")}).");

            // Stop: DesktopCapture finishes the current piece and exits; a stuck one is killed with its whisper-server.
            using CancellationTokenRegistration registration = token.Register(() =>
            {
                try
                {
                    process.StandardInput.WriteLine("stop");
                    process.StandardInput.Flush();
                }
                catch (Exception)
                {
                }

                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(8));
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                        }
                    }
                    catch (Exception)
                    {
                    }
                });
            });

            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                Match match;
                if ((match = SegmentLine.Match(line)).Success)
                {
                    string text = TranscriptTextPipeline.CleanRecognizedText(match.Groups["text"].Value.Trim());
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        result.Segments.Add(new MediaSegment(Seconds(match.Groups["start"].Value), Seconds(match.Groups["end"].Value), text));
                    }
                }
                else if ((match = ProgressLine.Match(line)).Success)
                {
                    TimeSpan total = Seconds(match.Groups["total"].Value);
                    result.Duration = total > result.Duration ? total : result.Duration;
                    progress?.Report(new MediaTranscriptionProgress(Seconds(match.Groups["done"].Value), total));
                }
                else if ((match = StartLine.Match(line)).Success)
                {
                    result.Duration = Seconds(match.Groups["total"].Value);
                    progress?.Report(new MediaTranscriptionProgress(TimeSpan.Zero, result.Duration));
                    log?.Invoke("[media] " + line);
                }
                else if (line.StartsWith("[file-stt-error]", StringComparison.Ordinal))
                {
                    result.FailedChunks++;
                    string[] parts = line.Split(' ', 3);
                    result.LastError = parts.Length == 3 ? parts[2] : line;
                    log?.Invoke("[media] " + line);
                }
                else if (line.StartsWith("[file-error]", StringComparison.Ordinal))
                {
                    fileError = line["[file-error]".Length..].Trim();
                    log?.Invoke("[media] " + line);
                }
                else if ((match = DoneLine.Match(line)).Success)
                {
                    result.CostRub = double.TryParse(match.Groups["cost"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double cost) ? cost : 0;
                    log?.Invoke("[media] " + line);
                }
                else if (!string.IsNullOrWhiteSpace(line))
                {
                    log?.Invoke("[media] " + line);
                }
            }

            await process.WaitForExitAsync(CancellationToken.None);
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || fileError.Length > 0)
            {
                string reason = fileError.Length > 0 ? fileError : FirstLines(stderr.ToString());
                throw new InvalidOperationException(reason.Length > 0 ? reason : "DesktopCapture exit code " + process.ExitCode);
            }
        }

        if (result.Duration == TimeSpan.Zero && result.Segments.Count > 0)
        {
            result.Duration = result.Segments[^1].End;
        }

        return result;
    }

    /// <summary>00:42 or 1:02:05.</summary>
    public static string FormatTime(TimeSpan value) =>
        value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : value.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    /// <summary>A file: link that a Markdown link can carry: spaces and brackets escaped.</summary>
    public static string FileLink(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri.Replace("(", "%28").Replace(")", "%29");

    private static IEnumerable<string> BuildArguments(MediaTranscriptionRequest request)
    {
        yield return "--transcribe-file";
        yield return request.MediaPath;
        yield return "--lang";
        yield return request.Language;
        yield return "--silence-threshold";
        yield return request.SilenceThreshold.ToString("0.####", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(request.SttDir))
        {
            yield return "--stt-dir";
            yield return request.SttDir;
        }

        // Local whisper also backs up the API when a piece fails there.
        if (!string.IsNullOrWhiteSpace(request.WhisperBin) && !string.IsNullOrWhiteSpace(request.WhisperModel))
        {
            yield return "--whisper-bin";
            yield return request.WhisperBin;
            yield return "--whisper-model";
            yield return request.WhisperModel;
        }

        string glossary = string.Join(", ", (request.SttGlossary ?? string.Empty)
            .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (glossary.Length > 0)
        {
            yield return "--stt-glossary";
            yield return glossary;
        }

        if (request.UseApiStt)
        {
            yield return "--stt-provider";
            yield return "api";
            yield return "--stt-api-url";
            yield return request.SttApiUrl;
            yield return "--stt-api-model";
            yield return request.SttApiModel;
        }
    }

    private static TimeSpan Seconds(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;

    private static string FirstLines(string text) => string.Join(" ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3));
}
