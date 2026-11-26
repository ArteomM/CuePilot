using System;
using System.Globalization;
using System.IO;
using System.Threading;
#if WINDOWS
using NAudio.CoreAudioApi;
#endif
using NAudio.Wave;

namespace DesktopCapture
{
    /// <summary>
    /// «--meter SECONDS»: the sound check of Settings. Listens to the microphone and to the computer's sound and prints
    /// «[level] system=0.123 mic=0.045» ten times a second (peak, 0–1), without chunks, recognition or a session.
    /// Stops after SECONDS, on Enter or when stdin closes. «--play FILE» plays a WAV on the chosen output device
    /// (Windows) while the levels run, so the bar of the computer's sound shows that this device is the one heard.
    /// </summary>
    partial class Program
    {
        private static int RunMeter(int seconds, string playFile)
        {
            float systemPeak = 0, micPeak = 0;
            IDisposable player = null;
            object gate = new object();
            var stop = new ManualResetEvent(false);
            IWaveIn system = null, mic = null;
            try
            {
                try
                {
                    system = AudioSources.CreateSystemCapture();
                    system.DataAvailable += (_, e) => { float p = Peak(e.Buffer, e.BytesRecorded, system.WaveFormat); lock (gate) { systemPeak = Math.Max(systemPeak, p); } };
                    system.StartRecording();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[level-error] system " + ex.Message);
                    system = null;
                }

                try
                {
                    mic = AudioSources.CreateMicCapture();
                    mic.DataAvailable += (_, e) => { float p = Peak(e.Buffer, e.BytesRecorded, mic.WaveFormat); lock (gate) { micPeak = Math.Max(micPeak, p); } };
                    mic.StartRecording();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[level-error] mic " + ex.Message);
                    mic = null;
                }

                player = StartMelody(playFile);
                new Thread(() =>
                {
                    try
                    {
                        Console.In.ReadLine();
                    }
                    catch (Exception)
                    {
                    }

                    stop.Set();
                }) { IsBackground = true }.Start();

                DateTime until = DateTime.UtcNow.AddSeconds(Math.Clamp(seconds, 1, 120));
                while (DateTime.UtcNow < until && !stop.WaitOne(100))
                {
                    float s, m;
                    lock (gate)
                    {
                        s = systemPeak;
                        m = micPeak;
                        systemPeak = micPeak = 0;
                    }

                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "[level] system={0:0.000} mic={1:0.000}", s, m));
                }

                return 0;
            }
            finally
            {
                player?.Dispose();
                foreach (IWaveIn capture in new[] { system, mic })
                {
                    if (capture == null)
                    {
                        continue;
                    }

                    try
                    {
                        capture.StopRecording();
                    }
                    catch (Exception)
                    {
                    }

                    capture.Dispose();
                }
            }
        }

        private static IDisposable StartMelody(string path)
        {
#if WINDOWS
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    MMDevice device = AudioSources.OutputDevice(enumerator);
                    var reader = new AudioFileReader(path);
                    var output = device != null ? new WasapiOut(device, AudioClientShareMode.Shared, true, 100) : new WasapiOut();
                    output.Init(reader);
                    output.Play();
                    return new Melody(output, reader);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[play-error] " + ex.Message);
                return null;
            }
#else
            return null;
#endif
        }

        private sealed class Melody : IDisposable
        {
            private readonly IWavePlayer _output;
            private readonly IDisposable _reader;

            public Melody(IWavePlayer output, IDisposable reader)
            {
                _output = output;
                _reader = reader;
            }

            public void Dispose()
            {
                try
                {
                    _output.Stop();
                }
                catch (Exception)
                {
                }

                _output.Dispose();
                _reader.Dispose();
            }
        }

        /// <summary>Loudest sample of a buffer, 0–1, for 32-bit float and 16-bit PCM.</summary>
        private static float Peak(byte[] buffer, int bytes, WaveFormat format)
        {
            float peak = 0;
            if (format.Encoding == WaveFormatEncoding.IeeeFloat || (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32))
            {
                for (int i = 0; i + 3 < bytes; i += 4)
                {
                    float v = Math.Abs(BitConverter.ToSingle(buffer, i));
                    if (v > peak && !float.IsNaN(v))
                    {
                        peak = v;
                    }
                }
            }
            else if (format.BitsPerSample == 16)
            {
                for (int i = 0; i + 1 < bytes; i += 2)
                {
                    float v = Math.Abs(BitConverter.ToInt16(buffer, i) / 32768f);
                    if (v > peak)
                    {
                        peak = v;
                    }
                }
            }

            return Math.Min(1f, peak);
        }
    }
}
