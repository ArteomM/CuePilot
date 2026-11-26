using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
#if WINDOWS
using NAudio.CoreAudioApi;
#endif
using NAudio.Wave;

namespace DesktopCapture
{
    /// <summary>
    /// The two audio streams DesktopCapture records. Windows: WASAPI loopback and the default microphone.
    /// macOS: the replio-audio helper (ScreenCaptureKit for system audio, AVAudioEngine for the microphone),
    /// which sits next to DesktopCapture and streams raw 32-bit float mono PCM to stdout.
    /// </summary>
    internal static class AudioSources
    {
        public static IWaveIn CreateSystemCapture()
        {
#if WINDOWS
            return new WasapiLoopbackCapture();
#else
            return new HelperProcessCapture("system");
#endif
        }

        /// <summary>
        /// The microphone for calls, or the first one that opens. A Bluetooth headset whose microphone cannot connect
        /// (0x800704D6, often busy with a phone or another app) used to crash the whole recording on start; now the
        /// next microphone is used («[mic-fallback]»), and with none at all the recording goes on with the computer's
        /// sound only (the caller catches the exception).
        /// </summary>
        public static IWaveIn CreateMicCapture()
        {
#if WINDOWS
            using (var enumerator = new MMDeviceEnumerator())
            {
                var candidates = new System.Collections.Generic.List<MMDevice>();
                void Add(Func<MMDevice> get)
                {
                    try
                    {
                        MMDevice device = get();
                        if (device != null && !candidates.Exists(c => c.ID == device.ID))
                        {
                            candidates.Add(device);
                        }
                    }
                    catch (Exception)
                    {
                        // No default device of this role.
                    }
                }

                Add(() => enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications));
                Add(() => enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia));
                foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                {
                    MMDevice each = device;
                    Add(() => each);
                }

                Exception last = null;
                foreach (MMDevice device in candidates)
                {
                    try
                    {
                        // WASAPI opens the device only on start: try it before handing it out.
                        using (var probe = new WasapiCapture(device))
                        {
                            probe.StartRecording();
                            probe.StopRecording();
                        }

                        if (!ReferenceEquals(device, candidates[0]))
                        {
                            Console.WriteLine("[mic-fallback] " + candidates[0].FriendlyName + " → " + device.FriendlyName);
                        }

                        return new WasapiCapture(device);
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        Console.WriteLine("[mic-error] " + device.FriendlyName + ": " + ex.Message);
                    }
                }

                throw new InvalidOperationException("no microphone could be opened", last);
            }
#else
            return new HelperProcessCapture("mic");
#endif
        }
    }

    /// <summary>Reads the default output device state before and after capture (Windows only; elsewhere Read throws).</summary>
    internal sealed class RenderEndpointProbe : IDisposable
    {
#if WINDOWS
        private readonly MMDeviceEnumerator _enumerator = new MMDeviceEnumerator();

        public Program.AudioEndpointSnapshot Read() =>
            Program.AudioEndpointSnapshot.From(_enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia));

        public void Dispose() => _enumerator.Dispose();
#else
        public Program.AudioEndpointSnapshot Read() =>
            throw new PlatformNotSupportedException("output device state is not read on this platform");

        public void Dispose()
        {
        }
#endif
    }

    /// <summary>
    /// One stream of the macOS helper: «replio-audio system» or «replio-audio mic».
    /// The helper writes 48 kHz mono float32 to stdout and stops when its stdin closes.
    /// </summary>
    internal sealed class HelperProcessCapture : IWaveIn
    {
        public const string HelperName = "replio-audio";
        private const int SampleRate = 48000;

        private readonly string _source;
        private Process _process;
        private Thread _reader;
        private volatile bool _stopping;

        public HelperProcessCapture(string source)
        {
            _source = source;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; set; }

        public event EventHandler<WaveInEventArgs> DataAvailable;

        public event EventHandler<StoppedEventArgs> RecordingStopped;

        public static string HelperPath => Path.Combine(AppContext.BaseDirectory, HelperName);

        public void StartRecording()
        {
            if (!File.Exists(HelperPath))
            {
                throw new FileNotFoundException("The audio helper was not found next to DesktopCapture.", HelperPath);
            }

            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = HelperPath,
                    Arguments = _source,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    Console.WriteLine("[audio-" + _source + "] " + e.Data);
                }
            };
            _process.Start();
            _process.BeginErrorReadLine();
            AppDomain.CurrentDomain.ProcessExit += (_, __) => KillHelper();

            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "audio-" + _source };
            _reader.Start();
        }

        public void StopRecording()
        {
            _stopping = true;
            try
            {
                // Closing stdin asks the helper to stop; it flushes and exits, which ends ReadLoop.
                _process?.StandardInput.Close();
            }
            catch
            {
            }

            if (_reader != null && !_reader.Join(2000))
            {
                KillHelper();
                _reader.Join(1000);
            }
        }

        public void Dispose()
        {
            if (!_stopping)
            {
                StopRecording();
            }

            KillHelper();
            _process?.Dispose();
        }

        private void ReadLoop()
        {
            Exception error = null;
            try
            {
                Stream stdout = _process.StandardOutput.BaseStream;
                var buffer = new byte[SampleRate / 10 * 4]; // ~100 ms
                int pending = 0;
                while (true)
                {
                    int read = stdout.Read(buffer, pending, buffer.Length - pending);
                    if (read <= 0)
                    {
                        break;
                    }

                    // Only whole float samples go out; a split sample waits for the next read.
                    int total = pending + read;
                    int whole = total - total % 4;
                    if (whole > 0)
                    {
                        var chunk = new byte[whole];
                        Buffer.BlockCopy(buffer, 0, chunk, 0, whole);
                        DataAvailable?.Invoke(this, new WaveInEventArgs(chunk, whole));
                    }

                    pending = total - whole;
                    if (pending > 0)
                    {
                        Buffer.BlockCopy(buffer, whole, buffer, 0, pending);
                    }
                }

                _process.WaitForExit(2000);
                if (!_stopping && _process.HasExited && _process.ExitCode != 0)
                {
                    error = new InvalidOperationException(DescribeExit(_process.ExitCode));
                }
            }
            catch (Exception ex)
            {
                if (!_stopping)
                {
                    error = ex;
                }
            }

            RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
        }

        private string DescribeExit(int code) => code switch
        {
            2 => _source == "mic"
                ? "microphone access is not allowed (System Settings → Privacy & Security → Microphone)"
                : "screen and system audio recording is not allowed (System Settings → Privacy & Security → Screen & System Audio Recording)",
            3 => "this macOS version cannot capture system audio (macOS 13 or newer is needed)",
            _ => HelperName + " " + _source + " exited with code " + code
        };

        private void KillHelper()
        {
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.Kill();
                }
            }
            catch
            {
            }
        }
    }
}
