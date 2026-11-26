using System;
using System.Collections.Generic;
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
    /// The two audio streams DesktopCapture records. Windows: WASAPI loopback of an output device and a microphone.
    /// macOS: the replio-audio helper (ScreenCaptureKit for system audio, AVAudioEngine for the microphone),
    /// which sits next to DesktopCapture and streams raw 32-bit float mono PCM to stdout.
    /// The devices chosen in Settings come first («--mic-device», «--system-device»). A device that does not open
    /// never stops the recording: the next one is used, and with none at all that stream stays silent
    /// («[mic-error]», «[system-error]», «[mic-fallback]», «[system-fallback]» tell the app).
    /// </summary>
    internal static class AudioSources
    {
        /// <summary>Settings: the microphone (Windows endpoint id, macOS device UID); empty = the system default.</summary>
        public static string PreferredMicId { get; set; } = string.Empty;

        /// <summary>Settings: the output device whose sound is recorded (Windows only); empty = the system default.</summary>
        public static string PreferredOutputId { get; set; } = string.Empty;

        public static IWaveIn CreateSystemCapture()
        {
#if WINDOWS
            using (var enumerator = new MMDeviceEnumerator())
            {
                List<MMDevice> candidates = Candidates(enumerator, DataFlow.Render, PreferredOutputId, Role.Multimedia, Role.Console);
                foreach (MMDevice device in candidates)
                {
                    try
                    {
                        // WASAPI opens the device only on start: try it before handing it out.
                        using (var probe = new WasapiLoopbackCapture(device))
                        {
                            probe.StartRecording();
                            probe.StopRecording();
                        }

                        if (!ReferenceEquals(device, candidates[0]))
                        {
                            Console.WriteLine("[system-fallback] " + candidates[0].FriendlyName + " → " + device.FriendlyName);
                        }

                        return new WasapiLoopbackCapture(device);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[system-error] " + device.FriendlyName + ": " + ex.Message);
                    }
                }

                Console.WriteLine("[system-error] no output device could be recorded; the computer sound stays silent.");
                return new SilentCapture();
            }
#else
            return new HelperProcessCapture("system");
#endif
        }

        /// <summary>
        /// The chosen microphone, then the one for calls, the default one and any other active one. A Bluetooth
        /// headset whose microphone cannot connect (0x800704D6, busy with a phone or another app) used to crash
        /// the whole recording on start. With no microphone at all the caller records the computer's sound only.
        /// </summary>
        public static IWaveIn CreateMicCapture()
        {
#if WINDOWS
            using (var enumerator = new MMDeviceEnumerator())
            {
                List<MMDevice> candidates = Candidates(enumerator, DataFlow.Capture, PreferredMicId, Role.Communications, Role.Multimedia);
                Exception last = null;
                foreach (MMDevice device in candidates)
                {
                    try
                    {
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
            return new HelperProcessCapture("mic", PreferredMicId);
#endif
        }

#if WINDOWS
        /// <summary>The chosen device (if it is active), then the defaults of the given roles, then every other active one.</summary>
        private static List<MMDevice> Candidates(MMDeviceEnumerator enumerator, DataFlow flow, string preferredId, params Role[] roles)
        {
            var list = new List<MMDevice>();
            void Add(Func<MMDevice> get)
            {
                try
                {
                    MMDevice device = get();
                    if (device != null && device.State == DeviceState.Active && !list.Exists(d => d.ID == device.ID))
                    {
                        list.Add(device);
                    }
                }
                catch (Exception)
                {
                    // No such device or no default of this role.
                }
            }

            if (!string.IsNullOrWhiteSpace(preferredId))
            {
                Add(() => enumerator.GetDevice(preferredId));
            }

            foreach (Role role in roles)
            {
                Add(() => enumerator.GetDefaultAudioEndpoint(flow, role));
            }

            foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                MMDevice each = device;
                Add(() => each);
            }

            return list;
        }

        /// <summary>The output device for the sound check melody: the chosen one, else the default.</summary>
        public static MMDevice OutputDevice(MMDeviceEnumerator enumerator) =>
            Candidates(enumerator, DataFlow.Render, PreferredOutputId, Role.Multimedia).Find(_ => true);
#endif

        /// <summary>
        /// «--list-devices»: one line per device, «[device]&lt;TAB&gt;input|output&lt;TAB&gt;1|0 (default)&lt;TAB&gt;id&lt;TAB&gt;name».
        /// macOS lists the microphones of the helper; the computer's sound there is all apps at once, so no outputs.
        /// </summary>
        public static int ListDevices()
        {
#if WINDOWS
            using (var enumerator = new MMDeviceEnumerator())
            {
                foreach ((DataFlow flow, string kind, Role role) in new[] { (DataFlow.Capture, "input", Role.Communications), (DataFlow.Render, "output", Role.Multimedia) })
                {
                    string defaultId = string.Empty;
                    try
                    {
                        defaultId = enumerator.GetDefaultAudioEndpoint(flow, role).ID;
                    }
                    catch (Exception)
                    {
                    }

                    foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
                    {
                        Console.WriteLine("[device]\t" + kind + "\t" + (device.ID == defaultId ? "1" : "0") + "\t" + device.ID + "\t" + device.FriendlyName);
                    }
                }
            }

            return 0;
#else
            using (var helper = Process.Start(new ProcessStartInfo(HelperProcessCapture.HelperPath, "list-devices")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }))
            {
                string output = helper.StandardOutput.ReadToEnd();
                helper.WaitForExit(5000);
                Console.Write(output);
                return helper.ExitCode;
            }
#endif
        }
    }

    /// <summary>A stream that never delivers sound: stands in for a device that could not be opened.</summary>
    internal sealed class SilentCapture : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);

#pragma warning disable CS0067 // Never raised: there is no sound.
        public event EventHandler<WaveInEventArgs> DataAvailable;
#pragma warning restore CS0067

        public event EventHandler<StoppedEventArgs> RecordingStopped;

        public void StartRecording()
        {
        }

        public void StopRecording() => RecordingStopped?.Invoke(this, new StoppedEventArgs(null));

        public void Dispose()
        {
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

        private readonly string _device;

        public HelperProcessCapture(string source, string device = "")
        {
            _source = source;
            _device = device ?? string.Empty;
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
                    // «mic <UID>»: the microphone chosen in Settings (the helper falls back to the default one).
                    Arguments = _device.Length > 0 ? _source + " \"" + _device.Replace("\"", string.Empty) + "\"" : _source,
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
