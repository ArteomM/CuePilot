using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CuePilotDesktop.Services;

public class CaptureOrchestratorService
{
    private Process? _process;

    private const string SttApiKeyEnvironmentVariable = "CUEPILOT_STT_API_KEY";

    private static readonly Regex StatsRegex = new(
        @"system\.avg=(?<sysAvg>[0-9\.,]+)KB/s.*system\.peak=(?<sysPeak>[0-9\.,]+).*mic\.avg=(?<micAvg>[0-9\.,]+)KB/s.*mic\.peak=(?<micPeak>[0-9\.,]+)(?:.*stt\.queue=(?<sttQueue>\d+))?(?:.*stt\.lag=(?<sttLag>[0-9\.,]+)s)?(?:.*stt\.cost=(?<sttCost>[0-9\.,]+))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SttRegex = new(
        @"^\[stt\]\[(?<source>system|mic)\]\s*(?<text>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public event EventHandler<string>? LogLine;
    public event EventHandler<CaptureStats>? StatsUpdated;
    public event EventHandler<TranscriptLine>? TranscriptReceived;
    public event EventHandler<bool>? RunningStateChanged;

    public bool IsRunning
    {
        get
        {
            try
            {
                return _process != null && !_process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public void Start(CaptureStartOptions options)
    {
        if (IsRunning)
        {
            return;
        }

        string captureArgs = BuildCaptureArguments(options);

        // Installed copy: DesktopCapture.exe next to the app. Source checkout: the built exe, otherwise dotnet run.
        string? captureExe = new[]
        {
            Path.Combine(options.WorkingDirectory, PlatformPaths.CaptureExecutableName),
            Path.Combine(options.WorkingDirectory, "bin", "Debug", PlatformPaths.CaptureTargetFramework, PlatformPaths.CaptureExecutableName),
            Path.Combine(options.WorkingDirectory, "bin", "Release", PlatformPaths.CaptureTargetFramework, PlatformPaths.CaptureExecutableName)
        }.FirstOrDefault(File.Exists);
        bool useBuiltExecutable = captureExe != null;
        string fileName = captureExe ?? "dotnet";
        string args = useBuiltExecutable ? captureArgs : PlatformPaths.CaptureDotnetRunPrefix + captureArgs;

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                WorkingDirectory = options.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };
        _process.StartInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        if (options.UseApiStt)
        {
            // Passed through the environment so the key never shows up in the process command line.
            _process.StartInfo.Environment[SttApiKeyEnvironmentVariable] = options.SttApiKey;
        }
        LogLine?.Invoke(this, useBuiltExecutable
            ? "[service] Starting DesktopCapture executable."
            : "[service] Starting DesktopCapture via dotnet run.");

        _process.OutputDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                HandleLine(eventArgs.Data);
            }
        };
        _process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                HandleLine("[stderr] " + eventArgs.Data);
            }
        };
        _process.Exited += (sender, _) =>
        {
            RunningStateChanged?.Invoke(this, false);
            LogLine?.Invoke(this, "[service] Capture process exited.");
            if (sender is Process exited)
            {
                exited.Dispose();
            }
        };

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        RunningStateChanged?.Invoke(this, true);
    }

    public void Stop()
    {
        Process? process = _process;
        if (!IsRunning || process == null)
        {
            return;
        }

        try
        {
            // DesktopCapture stops on Enter: it drains the STT queue and removes its temp files.
            process.StandardInput.WriteLine();
            process.StandardInput.Flush();
        }
        catch (Exception ex)
        {
            LogLine?.Invoke(this, "[service] Graceful stop failed: " + ex.Message);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    LogLine?.Invoke(this, "[service] Stop failed: " + ex.Message);
                }
            }
            catch (InvalidOperationException)
            {
                // Process already exited and was disposed.
            }
        });
    }

    public void Kill()
    {
        try
        {
            if (IsRunning)
            {
                _process?.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            LogLine?.Invoke(this, "[service] Kill failed: " + ex.Message);
        }
    }

    private void HandleLine(string line)
    {
        var statsMatch = StatsRegex.Match(line);
        if (statsMatch.Success)
        {
            // Stats arrive every second: they are parsed, not logged.
            StatsUpdated?.Invoke(
                this,
                new CaptureStats
                {
                    SystemAverageKb = ParseDouble(statsMatch.Groups["sysAvg"].Value),
                    SystemPeak = ParseDouble(statsMatch.Groups["sysPeak"].Value),
                    MicAverageKb = ParseDouble(statsMatch.Groups["micAvg"].Value),
                    MicPeak = ParseDouble(statsMatch.Groups["micPeak"].Value),
                    SttQueueLength = statsMatch.Groups["sttQueue"].Success ? (int)ParseDouble(statsMatch.Groups["sttQueue"].Value) : 0,
                    SttLagSeconds = statsMatch.Groups["sttLag"].Success ? ParseDouble(statsMatch.Groups["sttLag"].Value) : 0,
                    SttCostRub = statsMatch.Groups["sttCost"].Success ? ParseDouble(statsMatch.Groups["sttCost"].Value) : 0
                });
            return;
        }

        var sttMatch = SttRegex.Match(line);
        if (!sttMatch.Success)
        {
            LogLine?.Invoke(this, line);
        }
        else
        {
            string sourceRaw = sttMatch.Groups["source"].Value.ToLowerInvariant();
            string text = sttMatch.Groups["text"].Value.Trim();
            string cleanedText = TranscriptTextPipeline.CleanRecognizedText(text);
            string sourceSpeaker = sourceRaw == "system" ? "SYSTEM" : "MIC";
            if (!string.IsNullOrWhiteSpace(cleanedText))
            {
                TranscriptSource source = sourceRaw == "system" ? TranscriptSource.System : TranscriptSource.Mic;
                string formatted = TranscriptTextPipeline.BuildFormattedLine(source, cleanedText);
                TranscriptReceived?.Invoke(
                    this,
                    new TranscriptLine
                    {
                        Source = source,
                        RawText = text,
                        SpeakerName = sourceSpeaker,
                        Text = cleanedText,
                        FormattedText = formatted,
                        SummaryReadyText = TranscriptTextPipeline.BuildSummaryReadyText(sourceSpeaker, cleanedText),
                        Timestamp = DateTime.Now
                    });
            }
        }
    }

    private static string BuildCaptureArguments(CaptureStartOptions options)
    {
        var args = new List<string>
        {
            "--chunk-seconds " + options.ChunkSeconds,
            "--max-chunk-seconds " + options.MaxChunkSeconds,
            "--lang " + options.Language,
            "--silence-threshold " + options.SilenceThreshold.ToString("0.####", CultureInfo.InvariantCulture),
            "--max-stt-lag " + options.MaxSttLagSeconds
        };

        if (!string.IsNullOrWhiteSpace(options.SttDir))
        {
            args.Add("--stt-dir " + Quote(options.SttDir));
        }

        if (options.KeepChunks)
        {
            args.Add("--keep-chunks");
        }

        bool hasLocalWhisper = !string.IsNullOrWhiteSpace(options.WhisperBin) &&
            !string.IsNullOrWhiteSpace(options.WhisperModel);
        if (hasLocalWhisper)
        {
            args.Add("--whisper-bin " + Quote(options.WhisperBin));
            args.Add("--whisper-model " + Quote(options.WhisperModel));
        }

        string glossary = string.Join(", ", (options.SttGlossary ?? string.Empty)
            .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (glossary.Length > 0)
        {
            args.Add("--stt-glossary " + Quote(glossary));
        }

        if (options.UseApiStt)
        {
            args.Add("--stt-provider api");
            args.Add("--stt-api-url " + Quote(options.SttApiUrl));
            args.Add("--stt-api-model " + Quote(options.SttApiModel));
        }

        return string.Join(" ", args);
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", string.Empty) + "\"";
    }

    private static double ParseDouble(string value)
    {
        string normalized = value.Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : 0d;
    }
}

public class CaptureStartOptions
{
    public string WorkingDirectory { get; set; } = string.Empty;
    public string WhisperBin { get; set; } = string.Empty;
    public string WhisperModel { get; set; } = string.Empty;
    public string SttDir { get; set; } = string.Empty;
    public int ChunkSeconds { get; set; } = 5;
    public int MaxChunkSeconds { get; set; } = 10;
    public string Language { get; set; } = "ru";
    public double SilenceThreshold { get; set; } = 0.004;
    public int MaxSttLagSeconds { get; set; } = 60;
    public bool KeepChunks { get; set; }
    public bool UseApiStt { get; set; }
    public string SttApiUrl { get; set; } = string.Empty;
    public string SttApiModel { get; set; } = string.Empty;
    public string SttApiKey { get; set; } = string.Empty;
    public string SttGlossary { get; set; } = string.Empty;
}

public class CaptureStats
{
    public double SystemAverageKb { get; set; }
    public double SystemPeak { get; set; }
    public double MicAverageKb { get; set; }
    public double MicPeak { get; set; }
    public int SttQueueLength { get; set; }
    public double SttLagSeconds { get; set; }

    /// <summary>Cumulative API transcription cost of the current capture process, RUB.</summary>
    public double SttCostRub { get; set; }
}

public class TranscriptLine
{
    public TranscriptSource Source { get; set; }
    public string SpeakerName { get; set; } = string.Empty;
    public string RawText { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string FormattedText { get; set; } = string.Empty;
    public string SummaryReadyText { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}

public enum TranscriptSource
{
    System,
    Mic
}
