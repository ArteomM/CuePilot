using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ReplioDesktop.Services;

/// <summary>
/// The sound check of Settings, like in video call apps: Replio plays a short melody and at the same time shows the
/// levels of the microphone and of the computer's sound (DesktopCapture «--meter»). Nothing is recorded, recognised
/// or saved, and no session is made. The melody is generated (a soft three-note chime); on Windows DesktopCapture plays
/// it on the chosen output device, on macOS afplay plays it.
/// </summary>
public sealed class SoundCheckService
{
    private static readonly Regex LevelRegex = new(@"^\[level\]\s+system=(?<s>[0-9.]+)\s+mic=(?<m>[0-9.]+)", RegexOptions.Compiled);

    private Process? _meter;
    private Process? _player;

    /// <summary>Levels 0–1 (system, mic), about ten times a second, on a background thread.</summary>
    public event Action<double, double>? LevelUpdated;

    /// <summary>The check ended (time is up, stopped, or the meter could not start).</summary>
    public event Action? Finished;

    public event Action<string>? LogLine;

    public bool IsRunning
    {
        get
        {
            try
            {
                return _meter != null && !_meter.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// <paramref name="micDevice"/>, <paramref name="outputDevice"/>: the devices chosen in Settings (empty: as in
    /// the system). On Windows DesktopCapture plays the melody itself on that output device; on macOS afplay does.
    /// </summary>
    public void Start(string workingDirectory, int seconds, string micDevice, string outputDevice)
    {
        if (IsRunning)
        {
            return;
        }

        string? exe = CaptureOrchestratorService.FindCaptureExecutable(workingDirectory);
        string args = "--meter " + seconds.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(micDevice))
        {
            args += " --mic-device \"" + micDevice.Replace("\"", string.Empty) + "\"";
        }

        if (OperatingSystem.IsWindows())
        {
            if (!string.IsNullOrWhiteSpace(outputDevice))
            {
                args += " --system-device \"" + outputDevice.Replace("\"", string.Empty) + "\"";
            }

            string? melody = WriteMelody();
            if (melody != null)
            {
                args += " --play \"" + melody + "\"";
            }
        }
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exe ?? "dotnet",
                Arguments = exe != null ? args : PlatformPaths.CaptureDotnetRunPrefix + args,
                WorkingDirectory = workingDirectory,
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
        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            Match match = LevelRegex.Match(e.Data);
            if (match.Success &&
                double.TryParse(match.Groups["s"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double system) &&
                double.TryParse(match.Groups["m"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double mic))
            {
                LevelUpdated?.Invoke(system, mic);
            }
            else
            {
                LogLine?.Invoke("[sound-check] " + e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                LogLine?.Invoke("[sound-check][stderr] " + e.Data);
            }
        };
        process.Exited += (_, _) => Finished?.Invoke();
        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _meter = process;
        }
        catch (Exception ex)
        {
            process.Dispose();
            LogLine?.Invoke("[sound-check] Meter did not start: " + ex.Message);
            Finished?.Invoke();
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            PlayMelodyMac();
        }
    }

    public void Stop()
    {
        StopPlayer();
        Process? process = _meter;
        if (process == null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.WriteLine();
                process.StandardInput.Flush();
                if (!process.WaitForExit(1500))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception)
        {
            // Already gone.
        }
    }

    private string? WriteMelody()
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "replio-sound-check.wav");
            File.WriteAllBytes(path, BuildMelody());
            return path;
        }
        catch (Exception ex)
        {
            LogLine?.Invoke("[sound-check] Melody not written: " + ex.Message);
            return null;
        }
    }

    private void PlayMelodyMac()
    {
        string? path = WriteMelody();
        if (path == null)
        {
            return;
        }

        try
        {
            StopPlayer();
            _player = Process.Start(new ProcessStartInfo("/usr/bin/afplay", "\"" + path + "\"") { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception ex)
        {
            LogLine?.Invoke("[sound-check] Melody not played: " + ex.Message);
        }
    }

    private void StopPlayer()
    {
        try
        {
            if (_player is { HasExited: false })
            {
                _player.Kill();
            }
        }
        catch (Exception)
        {
        }

        _player = null;
    }

    /// <summary>A soft chime: three rising notes, then the chord, twice (about 5 seconds), 44.1 kHz 16-bit mono WAV.</summary>
    public static byte[] BuildMelody()
    {
        const int rate = 44100;
        double[] notes = [523.25, 659.25, 783.99]; // C5 E5 G5
        var samples = new List<float>();
        void Tone(double[] freqs, double seconds)
        {
            int count = (int)(rate * seconds);
            for (int i = 0; i < count; i++)
            {
                double t = (double)i / rate;
                double envelope = Math.Min(1, t / 0.01) * Math.Exp(-3.2 * t);
                double value = 0;
                foreach (double f in freqs)
                {
                    value += Math.Sin(2 * Math.PI * f * t) + 0.25 * Math.Sin(4 * Math.PI * f * t);
                }

                samples.Add((float)(value / freqs.Length * envelope * 0.45));
            }
        }

        for (int round = 0; round < 2; round++)
        {
            foreach (double note in notes)
            {
                Tone([note], 0.32);
            }

            Tone(notes, 1.3);
            Tone([0], 0.25);
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        int dataBytes = samples.Count * 2;
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        foreach (float sample in samples)
        {
            writer.Write((short)Math.Clamp(sample * 32767, short.MinValue, short.MaxValue));
        }

        writer.Flush();
        return stream.ToArray();
    }
}
