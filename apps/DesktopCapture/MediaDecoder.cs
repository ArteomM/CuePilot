using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DesktopCapture
{
    /// <summary>
    /// The sound of an audio or video file as 16 kHz mono samples, read block by block so an hours-long file never
    /// sits in memory. Windows: Media Foundation (mp3, m4a, aac, wav, wma, mp4, mov, wmv, avi; mkv and flac on
    /// Windows 10+). macOS: AVFoundation through the bundled cuepilot-audio helper (mp3, m4a, aac, wav, aiff, mp4, mov).
    /// What these cannot open goes through ffmpeg when it is installed; macOS without ffmpeg falls back to the
    /// built-in afconvert (audio files, m4a and mp4).
    /// </summary>
    internal sealed class MediaDecoder : IDisposable
    {
        public const int SampleRate = 16000;

        private static readonly Regex FfmpegDuration = new Regex(@"Duration:\s*(\d+):(\d\d):(\d\d(?:\.\d+)?)", RegexOptions.Compiled);

        private readonly ISampleProvider _samples;
        private readonly IDisposable _reader;
        private readonly Process _ffmpeg;
        private readonly Queue<string> _ffmpegErrors = new Queue<string>();
        private readonly string _tempFile;
        private byte[] _pcm = Array.Empty<byte>();
        private int _pcmPending;
        private long _samplesRead;
        private double _durationSeconds;

        private MediaDecoder(string method, ISampleProvider samples, IDisposable reader, TimeSpan duration, string tempFile = null)
        {
            Method = method;
            _samples = samples;
            _reader = reader;
            _durationSeconds = duration.TotalSeconds;
            _tempFile = tempFile;
        }

        private MediaDecoder(Process ffmpeg)
        {
            Method = "ffmpeg";
            _ffmpeg = ffmpeg;
        }

        public string Method { get; }

        /// <summary>Length of the file; zero while unknown (ffmpeg reports it a moment after the start).</summary>
        public TimeSpan Duration => TimeSpan.FromSeconds(Volatile.Read(ref _durationSeconds));

        public static MediaDecoder Open(string path, string tempDirectory)
        {
            var errors = new List<string>();
            if (string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return FromReader("wav", new WaveFileReader(path));
                }
                catch (Exception ex)
                {
                    errors.Add("wav: " + ex.Message);
                }
            }

#if WINDOWS
            try
            {
                return FromReader("Media Foundation", new MediaFoundationReader(path));
            }
            catch (Exception ex)
            {
                errors.Add("Media Foundation: " + ex.Message);
            }
#endif

            string appleHelper = Path.Combine(AppContext.BaseDirectory, HelperProcessCapture.HelperName);
            if (OperatingSystem.IsMacOS() && File.Exists(appleHelper))
            {
                try
                {
                    return FromConverter("AVFoundation", appleHelper, wav => new[] { "decode", path, wav }, tempDirectory);
                }
                catch (Exception ex)
                {
                    errors.Add("AVFoundation: " + ex.Message);
                }
            }

            string ffmpeg = FindFfmpeg();
            if (ffmpeg != null)
            {
                try
                {
                    return FromFfmpeg(ffmpeg, path);
                }
                catch (Exception ex)
                {
                    errors.Add("ffmpeg: " + ex.Message);
                }
            }
            else
            {
                errors.Add("ffmpeg was not found");
            }

            if (OperatingSystem.IsMacOS() && File.Exists("/usr/bin/afconvert"))
            {
                try
                {
                    return FromConverter(
                        "afconvert",
                        "/usr/bin/afconvert",
                        wav => new[] { "-f", "WAVE", "-d", "LEI16@" + SampleRate.ToString(CultureInfo.InvariantCulture), "-c", "1", path, wav },
                        tempDirectory);
                }
                catch (Exception ex)
                {
                    errors.Add("afconvert: " + ex.Message);
                }
            }

            throw new InvalidOperationException("the sound of the file could not be read (" + string.Join("; ", errors) + ")");
        }

        /// <summary>Fills <paramref name="buffer"/> with 16 kHz mono samples; 0 at the end of the file.</summary>
        public int Read(float[] buffer, int offset, int count)
        {
            int read = _ffmpeg != null ? ReadFfmpeg(buffer, offset, count) : _samples.Read(buffer, offset, count);
            _samplesRead += read;
            return read;
        }

        public void Dispose()
        {
            _reader?.Dispose();
            if (_ffmpeg != null)
            {
                try
                {
                    if (!_ffmpeg.HasExited)
                    {
                        _ffmpeg.Kill();
                    }
                }
                catch
                {
                }

                _ffmpeg.Dispose();
            }

            if (_tempFile != null)
            {
                try
                {
                    File.Delete(_tempFile);
                }
                catch
                {
                }
            }
        }

        private static MediaDecoder FromReader(string method, WaveStream reader, string tempFile = null)
        {
            try
            {
                if (reader.WaveFormat.Channels < 1 || reader.WaveFormat.SampleRate <= 0)
                {
                    throw new InvalidDataException("the file has no sound track");
                }

                ISampleProvider samples = reader.ToSampleProvider();
                if (samples.WaveFormat.Channels > 1)
                {
                    samples = new MonoMix(samples);
                }

                if (samples.WaveFormat.SampleRate != SampleRate)
                {
                    samples = new WdlResamplingSampleProvider(samples, SampleRate);
                }

                return new MediaDecoder(method, samples, reader, reader.TotalTime, tempFile);
            }
            catch
            {
                reader.Dispose();
                throw;
            }
        }

        private static MediaDecoder FromFfmpeg(string ffmpeg, string path)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            foreach (string arg in new[] { "-nostdin", "-hide_banner", "-i", path, "-vn", "-sn", "-dn", "-ac", "1", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture), "-f", "s16le", "-acodec", "pcm_s16le", "pipe:1" })
            {
                process.StartInfo.ArgumentList.Add(arg);
            }

            var decoder = new MediaDecoder(process);
            process.ErrorDataReceived += (_, e) => decoder.OnFfmpegError(e.Data);
            process.Start();
            process.BeginErrorReadLine();
            return decoder;
        }

        /// <summary>
        /// A converter (the AVFoundation helper or afconvert) writes a temporary 16 kHz mono WAV that is then read like
        /// any WAV and deleted on Dispose.
        /// </summary>
        private static MediaDecoder FromConverter(string method, string tool, Func<string, string[]> arguments, string tempDirectory)
        {
            string wav = Path.Combine(tempDirectory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-file-" + Environment.ProcessId + "-" + method.ToLowerInvariant() + ".wav");
            using (var process = new Process())
            {
                process.StartInfo.FileName = tool;
                foreach (string arg in arguments(wav))
                {
                    process.StartInfo.ArgumentList.Add(arg);
                }

                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.Start();
                var stdout = process.StandardOutput.ReadToEndAsync();
                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                stdout.Wait();
                if (process.ExitCode != 0 || !File.Exists(wav))
                {
                    TryDeleteFile(wav);
                    throw new InvalidOperationException("exit code " + process.ExitCode + " " + stderr.Trim());
                }
            }

            try
            {
                return FromReader(method, new WaveFileReader(wav), wav);
            }
            catch
            {
                TryDeleteFile(wav);
                throw;
            }
        }

        private int ReadFfmpeg(float[] buffer, int offset, int count)
        {
            int needed = count * 2;
            if (_pcm.Length < needed)
            {
                Array.Resize(ref _pcm, needed);
            }

            Stream stdout = _ffmpeg.StandardOutput.BaseStream;
            while (true)
            {
                int read = stdout.Read(_pcm, _pcmPending, needed - _pcmPending);
                if (read <= 0)
                {
                    if (_ffmpeg.WaitForExit(5000))
                    {
                        // The parameterless wait also waits for the last stderr lines, which hold the reason.
                        _ffmpeg.WaitForExit();
                    }

                    if (_samplesRead == 0 && _ffmpeg.HasExited && _ffmpeg.ExitCode != 0)
                    {
                        string details;
                        lock (_ffmpegErrors)
                        {
                            details = string.Join(" ", _ffmpegErrors);
                        }

                        throw new InvalidOperationException("ffmpeg exit code " + _ffmpeg.ExitCode + (details.Length > 0 ? ": " + details : string.Empty));
                    }

                    return 0;
                }

                int total = _pcmPending + read;
                int samples = total / 2;
                if (samples == 0)
                {
                    _pcmPending = total;
                    continue;
                }

                for (int i = 0; i < samples; i++)
                {
                    buffer[offset + i] = BitConverter.ToInt16(_pcm, i * 2) / 32768f;
                }

                // A sample split between two reads waits for the next one.
                _pcmPending = total - samples * 2;
                if (_pcmPending > 0)
                {
                    _pcm[0] = _pcm[samples * 2];
                }

                return samples;
            }
        }

        private void OnFfmpegError(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            Match duration = FfmpegDuration.Match(line);
            if (duration.Success && Volatile.Read(ref _durationSeconds) == 0)
            {
                double seconds = int.Parse(duration.Groups[1].Value, CultureInfo.InvariantCulture) * 3600 +
                    int.Parse(duration.Groups[2].Value, CultureInfo.InvariantCulture) * 60 +
                    double.Parse(duration.Groups[3].Value, CultureInfo.InvariantCulture);
                Volatile.Write(ref _durationSeconds, seconds);
            }

            lock (_ffmpegErrors)
            {
                _ffmpegErrors.Enqueue(line.Trim());
                while (_ffmpegErrors.Count > 3)
                {
                    _ffmpegErrors.Dequeue();
                }
            }
        }

        /// <summary>ffmpeg next to DesktopCapture, on PATH, or where Homebrew puts it (a Mac app does not get the shell's PATH).</summary>
        private static string FindFfmpeg()
        {
            string name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
            var folders = new List<string> { AppContext.BaseDirectory };
            folders.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
            if (OperatingSystem.IsMacOS())
            {
                folders.Add("/opt/homebrew/bin");
                folders.Add("/usr/local/bin");
            }

            foreach (string folder in folders)
            {
                try
                {
                    string candidate = Path.Combine(folder.Trim().Trim('"'), name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry.
                }
            }

            return null;
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }

        /// <summary>Averages any number of channels into one.</summary>
        private sealed class MonoMix : ISampleProvider
        {
            private readonly ISampleProvider _source;
            private readonly int _channels;
            private float[] _buffer = Array.Empty<float>();

            public MonoMix(ISampleProvider source)
            {
                _source = source;
                _channels = source.WaveFormat.Channels;
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
            }

            public WaveFormat WaveFormat { get; }

            public int Read(float[] buffer, int offset, int count)
            {
                int needed = count * _channels;
                if (_buffer.Length < needed)
                {
                    _buffer = new float[needed];
                }

                int read = _source.Read(_buffer, 0, needed);
                int frames = read / _channels;
                for (int frame = 0; frame < frames; frame++)
                {
                    float sum = 0f;
                    int start = frame * _channels;
                    for (int channel = 0; channel < _channels; channel++)
                    {
                        sum += _buffer[start + channel];
                    }

                    buffer[offset + frame] = sum / _channels;
                }

                return frames;
            }
        }
    }
}
