using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
#if WINDOWS
using NAudio.CoreAudioApi;
#endif
using NAudio.Wave;

namespace DesktopCapture
{
    partial class Program
    {
        private const int WhisperTargetSampleRate = 16000;
        private const int VadFrameSamples = 480; // 30 ms at 16 kHz
        private const string SttApiKeyEnvironmentVariable = "CUEPILOT_STT_API_KEY";

        private static readonly HttpClient SttHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private static readonly HttpClient LocalWhisperHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        private static volatile string WhisperServerUrl;
        private static Process WhisperServerProcess;
        private static readonly SttMetrics Metrics = new SttMetrics();
        private static volatile bool SttApiUsesJsonBody;

        static void Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Console.WriteLine("DesktopCapture supports Windows (WASAPI loopback) and macOS (cuepilot-audio helper) only.");
                return;
            }

            var options = ParseOptions(args);
            if (!string.IsNullOrWhiteSpace(options.OutputPath))
            {
                options.OutputPath = Path.GetFullPath(options.OutputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath));
            }

            if (!string.IsNullOrWhiteSpace(options.WhisperBinPath))
            {
                options.WhisperBinPath = Path.GetFullPath(options.WhisperBinPath);
            }
            if (!string.IsNullOrWhiteSpace(options.WhisperModelPath))
            {
                options.WhisperModelPath = Path.GetFullPath(options.WhisperModelPath);
            }
            if (!string.IsNullOrWhiteSpace(options.SoakReportPath))
            {
                options.SoakReportPath = Path.GetFullPath(options.SoakReportPath);
                Directory.CreateDirectory(Path.GetDirectoryName(options.SoakReportPath));
            }

            if (options.UseApiStt)
            {
                options.SttApiKey = Environment.GetEnvironmentVariable(SttApiKeyEnvironmentVariable);
                if (string.IsNullOrWhiteSpace(options.SttApiKey) || string.IsNullOrWhiteSpace(options.SttApiUrl) || string.IsNullOrWhiteSpace(options.SttApiModel))
                {
                    Console.WriteLine("[stt-error] API transcription needs --stt-api-url, --stt-api-model and " + SttApiKeyEnvironmentVariable + ". Falling back to local whisper.");
                    options.UseApiStt = false;
                }
            }

            if (options.WhisperEnabled)
            {
                if (!File.Exists(options.WhisperBinPath))
                {
                    throw new FileNotFoundException("Whisper binary was not found.", options.WhisperBinPath);
                }

                if (!File.Exists(options.WhisperModelPath))
                {
                    throw new FileNotFoundException("Whisper model was not found.", options.WhisperModelPath);
                }

                StartWhisperServer(options);
            }

            string sttChunksDirectory = Path.GetFullPath(options.SttChunksDirectory ?? Path.Combine(".", "stt-chunks"));
            Directory.CreateDirectory(sttChunksDirectory);

            if (!string.IsNullOrWhiteSpace(options.TranscribeFilePath))
            {
                Environment.ExitCode = TranscribeFile(options, sttChunksDirectory);
                StopWhisperServer();
                return;
            }

            // Local whisper is CPU bound: one shared worker. API calls are I/O bound: one worker per source,
            // so system and mic are transcribed in parallel and do not wait for each other.
            var systemSttJobs = new BlockingCollection<SttJob>(boundedCapacity: 128);
            var micSttJobs = options.UseApiStt ? new BlockingCollection<SttJob>(boundedCapacity: 128) : systemSttJobs;
            bool run = true;
            bool stopRequested = false;
            bool hadSystemCaptureError = false;
            bool hadMicCaptureError = false;
            bool hadSttError = false;
            bool soakAutoStopTriggered = false;
            var stopSignal = new ManualResetEvent(false);
            options.SttErrorCounter = () => { hadSttError = true; };
            var sttWorkers = new List<Thread>
            {
                new Thread(() => SttWorkerLoop(systemSttJobs, options)) { IsBackground = true }
            };
            if (!ReferenceEquals(micSttJobs, systemSttJobs))
            {
                sttWorkers.Add(new Thread(() => SttWorkerLoop(micSttJobs, options)) { IsBackground = true });
            }

            foreach (Thread worker in sttWorkers)
            {
                worker.Start();
            }

            using (IWaveIn systemCapture = AudioSources.CreateSystemCapture())
            using (var renderEndpoint = new RenderEndpointProbe())
            {
                AudioEndpointSnapshot renderBefore = null;
                AudioEndpointSnapshot renderAfter = null;
                bool deviceCheckPass = false;
                bool deviceCheckEvaluated = false;
                try
                {
                    renderBefore = renderEndpoint.Read();
                    Console.WriteLine(
                        "[device-check][before] id={0} name=\"{1}\" vol={2:0.0}% muted={3} mix={4}Hz/{5}ch/{6}bit",
                        renderBefore.DeviceId,
                        renderBefore.DeviceName,
                        renderBefore.MasterVolumePercent,
                        renderBefore.IsMuted,
                        renderBefore.SampleRate,
                        renderBefore.Channels,
                        renderBefore.BitsPerSample);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[device-check] failed to read render endpoint before capture: " + ex.Message);
                }

                var systemStats = new StreamStats();
                var micStats = new StreamStats();
                var systemChunkState = new ChunkState(systemCapture.WaveFormat);
                ChunkState micChunkState = null;
                object sync = new object();
                DateTime startedAt = DateTime.UtcNow;
                int systemChunkIndex = 0;
                int micChunkIndex = 0;

                IWaveIn micCapture = null;
                try
                {
                    micCapture = AudioSources.CreateMicCapture();
                    micChunkState = new ChunkState(micCapture.WaveFormat);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Microphone capture is unavailable: " + ex.Message);
                }

                WaveFileWriter systemWriter = null;
                WaveFileWriter micWriter = null;
                if (!string.IsNullOrWhiteSpace(options.OutputPath))
                {
                    string systemPath = options.OutputPath;
                    string micPath = BuildMicPath(options.OutputPath);

                    systemWriter = new WaveFileWriter(systemPath, systemCapture.WaveFormat);
                    if (micCapture != null)
                    {
                        micWriter = new WaveFileWriter(micPath, micCapture.WaveFormat);
                    }
                }

                systemCapture.DataAvailable += (sender, eventArgs) =>
                {
                    if (systemWriter != null)
                    {
                        systemWriter.Write(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
                    }

                    var monoSamples = DecodeToMonoFloat(eventArgs.Buffer, eventArgs.BytesRecorded, systemCapture.WaveFormat);
                    float localPeak = FindPeak(monoSamples);

                    lock (sync)
                    {
                        systemStats.TotalBytes += eventArgs.BytesRecorded;
                        systemStats.Peak = localPeak > systemStats.Peak ? localPeak : systemStats.Peak;
                        systemChunkState.Samples.AddRange(monoSamples);
                    }
                };

                systemCapture.RecordingStopped += (sender, eventArgs) =>
                {
                    if (systemWriter != null)
                    {
                        systemWriter.Dispose();
                    }

                    if (eventArgs.Exception != null)
                    {
                        hadSystemCaptureError = true;
                        Console.WriteLine("System capture stopped with error: " + eventArgs.Exception.Message);
                    }
                };

                if (micCapture != null)
                {
                    micCapture.DataAvailable += (sender, eventArgs) =>
                    {
                        if (micWriter != null)
                        {
                            micWriter.Write(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
                        }

                        var monoSamples = DecodeToMonoFloat(eventArgs.Buffer, eventArgs.BytesRecorded, micCapture.WaveFormat);
                        float localPeak = FindPeak(monoSamples);
                        lock (sync)
                        {
                            micStats.TotalBytes += eventArgs.BytesRecorded;
                            micStats.Peak = localPeak > micStats.Peak ? localPeak : micStats.Peak;
                            micChunkState.Samples.AddRange(monoSamples);
                        }
                    };

                    micCapture.RecordingStopped += (sender, eventArgs) =>
                    {
                        if (micWriter != null)
                        {
                            micWriter.Dispose();
                        }

                        if (eventArgs.Exception != null)
                        {
                            hadMicCaptureError = true;
                            Console.WriteLine("Mic capture stopped with error: " + eventArgs.Exception.Message);
                        }
                    };
                }

                Console.WriteLine("CuePilot DesktopCapture prototype started.");
                Console.WriteLine("Capturing SYSTEM audio using WASAPI loopback + default microphone.");
                if (options.SoakMinutes > 0)
                {
                    Console.WriteLine("Soak test mode enabled: " + options.SoakMinutes + " minute(s).");
                    Console.WriteLine("Press Enter to stop early (will mark soak as incomplete).");
                }
                else
                {
                    Console.WriteLine("Press Enter to stop.");
                }
                Console.WriteLine("Chunk seconds: " + options.ChunkSeconds + "-" + options.MaxChunkSeconds + " (cut at pauses)");
                if (options.UseApiStt)
                {
                    Console.WriteLine("STT via API: " + options.SttApiUrl + " model=" + options.SttApiModel +
                        (options.WhisperEnabled ? " (local whisper fallback enabled)" : string.Empty));
                }
                else if (options.WhisperEnabled)
                {
                    Console.WriteLine("Whisper.cpp enabled.");
                    Console.WriteLine("Whisper bin: " + options.WhisperBinPath);
                    Console.WriteLine("Whisper model: " + options.WhisperModelPath);
                    Console.WriteLine(WhisperServerUrl != null
                        ? "Whisper server: " + WhisperServerUrl
                        : "Whisper server: not running, whisper-cli is started per chunk");
                }
                else
                {
                    Console.WriteLine("Whisper.cpp disabled. Use --whisper-bin + --whisper-model to enable.");
                }

                Console.WriteLine("Silence threshold: " + options.SilenceThreshold.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) +
                    ", max STT lag: " + options.MaxSttLagSeconds + "s, keep chunks: " + options.KeepChunks);

                if (!string.IsNullOrWhiteSpace(options.OutputPath))
                {
                    Console.WriteLine("Recording SYSTEM to: " + options.OutputPath);
                    if (micCapture != null)
                    {
                        Console.WriteLine("Recording MIC to: " + BuildMicPath(options.OutputPath));
                    }
                }

                if (micCapture == null)
                {
                    Console.WriteLine("Mic stream is disabled (default capture endpoint not found).");
                }

                systemCapture.StartRecording();
                if (micCapture != null)
                {
                    micCapture.StartRecording();
                }

                var statsThread = new Thread(() =>
                {
                    while (run)
                    {
                        Thread.Sleep(1000);

                        long systemBytes;
                        float systemPeak;
                        long micBytes;
                        float micPeak;
                        float[] systemChunk = null;
                        float[] micChunk = null;

                        lock (sync)
                        {
                            systemBytes = systemStats.TotalBytes;
                            systemPeak = systemStats.Peak;
                            systemStats.Peak = 0f;

                            micBytes = micStats.TotalBytes;
                            micPeak = micStats.Peak;
                            micStats.Peak = 0f;

                            systemChunk = TakeChunkAtPause(systemChunkState, options);
                            if (micChunkState != null)
                            {
                                micChunk = TakeChunkAtPause(micChunkState, options);
                            }
                        }

                        double seconds = (DateTime.UtcNow - startedAt).TotalSeconds;
                        double systemKbPerSec = seconds > 0 ? (systemBytes / 1024.0) / seconds : 0;
                        double micKbPerSec = seconds > 0 ? (micBytes / 1024.0) / seconds : 0;

                        Console.WriteLine(
                            string.Format(
                                System.Globalization.CultureInfo.InvariantCulture,
                                "[stats] elapsed={0:0.0}s system.avg={1:0.0}KB/s system.peak={2:0.000} mic.avg={3:0.0}KB/s mic.peak={4:0.000} stt.queue={5} stt.lag={6:0.0}s stt.skipped={7} stt.dropped={8} stt.cost={9:0.###}",
                                seconds,
                                systemKbPerSec,
                                systemPeak,
                                micKbPerSec,
                                micPeak,
                                systemSttJobs.Count + (ReferenceEquals(micSttJobs, systemSttJobs) ? 0 : micSttJobs.Count),
                                Metrics.CurrentLagSeconds(systemSttJobs, micSttJobs),
                                Metrics.SilentChunks,
                                Metrics.DroppedChunks,
                                Metrics.CostRub));

                        if (systemChunk != null)
                        {
                            EnqueueChunk(systemSttJobs, options, sttChunksDirectory, "system", ++systemChunkIndex, systemChunk, systemChunkState.SampleRate);
                        }

                        if (micChunk != null)
                        {
                            EnqueueChunk(micSttJobs, options, sttChunksDirectory, "mic", ++micChunkIndex, micChunk, micChunkState.SampleRate);
                        }
                    }
                });
                statsThread.IsBackground = true;
                statsThread.Start();

                if (options.SoakMinutes > 0)
                {
                    int soakMs = options.SoakMinutes * 60 * 1000;
                    var soakThread = new Thread(() =>
                    {
                        Thread.Sleep(soakMs);
                        if (!stopRequested)
                        {
                            stopRequested = true;
                            soakAutoStopTriggered = true;
                            stopSignal.Set();
                            Console.WriteLine("[soak] target duration reached, stopping capture.");
                        }
                    });
                    soakThread.IsBackground = true;
                    soakThread.Start();
                }

                var inputThread = new Thread(() =>
                {
                    Console.ReadLine();
                    if (!stopRequested)
                    {
                        stopRequested = true;
                        stopSignal.Set();
                    }
                });
                inputThread.IsBackground = true;
                inputThread.Start();

                stopSignal.WaitOne();
                run = false;

                systemCapture.StopRecording();
                if (micCapture != null)
                {
                    micCapture.StopRecording();
                }

                Thread.Sleep(250);
                systemSttJobs.CompleteAdding();
                if (!ReferenceEquals(micSttJobs, systemSttJobs))
                {
                    micSttJobs.CompleteAdding();
                }

                bool sttWorkerFinished = sttWorkers.All(worker => worker.Join(3000));
                if (!options.KeepChunks)
                {
                    DeleteLeftoverChunks(sttChunksDirectory);
                }

                Console.WriteLine("Capture finished.");
                if (!string.IsNullOrWhiteSpace(options.OutputPath))
                {
                    Console.WriteLine("Saved system file: " + options.OutputPath);
                    if (micCapture != null)
                    {
                        Console.WriteLine("Saved mic file: " + BuildMicPath(options.OutputPath));
                    }
                }

                if (micCapture != null)
                {
                    micCapture.Dispose();
                }

                try
                {
                    renderAfter = renderEndpoint.Read();
                    Console.WriteLine(
                        "[device-check][after]  id={0} name=\"{1}\" vol={2:0.0}% muted={3} mix={4}Hz/{5}ch/{6}bit",
                        renderAfter.DeviceId,
                        renderAfter.DeviceName,
                        renderAfter.MasterVolumePercent,
                        renderAfter.IsMuted,
                        renderAfter.SampleRate,
                        renderAfter.Channels,
                        renderAfter.BitsPerSample);

                    deviceCheckPass = true;
                    deviceCheckEvaluated = true;
                    if (renderBefore != null)
                    {
                        bool sameId = string.Equals(renderBefore.DeviceId, renderAfter.DeviceId, StringComparison.OrdinalIgnoreCase);
                        bool sameMute = renderBefore.IsMuted == renderAfter.IsMuted;
                        bool sameVolume = Math.Abs(renderBefore.MasterVolumePercent - renderAfter.MasterVolumePercent) < 0.01f;
                        bool sameFormat =
                            renderBefore.SampleRate == renderAfter.SampleRate &&
                            renderBefore.Channels == renderAfter.Channels &&
                            renderBefore.BitsPerSample == renderAfter.BitsPerSample;

                        if (sameId && sameMute && sameVolume && sameFormat)
                        {
                            Console.WriteLine("[device-check] PASS: capture did not change default output endpoint state.");
                        }
                        else
                        {
                            deviceCheckPass = false;
                            Console.WriteLine(
                                "[device-check] WARN: endpoint state changed. idChanged={0} volumeChanged={1} muteChanged={2} formatChanged={3}",
                                !sameId,
                                !sameVolume,
                                !sameMute,
                                !sameFormat);
                        }

                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[device-check] failed to read render endpoint after capture: " + ex.Message);
                }

                double elapsedMinutesFinal = (DateTime.UtcNow - startedAt).TotalMinutes;
                bool soakDurationReachedFinal = options.SoakMinutes > 0 && elapsedMinutesFinal >= (options.SoakMinutes - 0.05);
                bool soakPassFinal =
                    (options.SoakMinutes > 0) &&
                    soakDurationReachedFinal &&
                    deviceCheckPass &&
                    !hadSystemCaptureError &&
                    !hadMicCaptureError &&
                    !hadSttError;

                if (options.SoakMinutes > 0)
                {
                    Console.WriteLine(
                        "[soak] result={0} duration={1:0.00}m target={2}m systemErrors={3} micErrors={4} sttErrors={5} deviceCheck={6}",
                        soakPassFinal ? "PASS" : "FAIL",
                        elapsedMinutesFinal,
                        options.SoakMinutes,
                        hadSystemCaptureError,
                        hadMicCaptureError,
                        hadSttError,
                        deviceCheckPass);
                }

                if (!string.IsNullOrWhiteSpace(options.SoakReportPath))
                {
                    WriteSoakReport(
                        options,
                        startedAt,
                        DateTime.UtcNow,
                        soakAutoStopTriggered,
                        stopRequested && !soakAutoStopTriggered,
                        hadSystemCaptureError,
                        hadMicCaptureError,
                        hadSttError,
                        sttWorkerFinished,
                        deviceCheckEvaluated,
                        deviceCheckPass,
                        renderBefore,
                        renderAfter,
                        systemChunkIndex,
                        micChunkIndex,
                        soakDurationReachedFinal,
                        soakPassFinal);
                }
            }
        }

        private static void EnqueueChunk(
            BlockingCollection<SttJob> sttJobs,
            RuntimeOptions options,
            string chunksDirectory,
            string source,
            int chunkIndex,
            float[] sourceSamples,
            int sourceSampleRate)
        {
            float[] resampled = ResampleTo16000(sourceSamples, sourceSampleRate);
            if (resampled.Length == 0)
            {
                return;
            }

            bool sttEnabled = options.WhisperEnabled || options.UseApiStt;

            // Silent chunks are not sent to STT: whisper hallucinates on silence ("Редактор субтитров ...")
            // and they cost CPU time or API money for nothing.
            if (sttEnabled && SpeechLevel(resampled) < options.SilenceThreshold)
            {
                Interlocked.Increment(ref Metrics.SilentChunks);
                return;
            }

            string baseName = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + source + "-" + chunkIndex.ToString("D4");
            string wavPath = Path.Combine(chunksDirectory, baseName + ".wav");
            WriteMono16kWav(wavPath, resampled);

            if (sttEnabled)
            {
                if (!sttJobs.TryAdd(new SttJob
                {
                    Source = source,
                    WavPath = wavPath,
                    CreatedAtUtc = DateTime.UtcNow
                }))
                {
                    Interlocked.Increment(ref Metrics.DroppedChunks);
                    Console.WriteLine("[stt-drop] queue full, dropping chunk from " + source);
                    DeleteChunkFiles(wavPath, options);
                }
            }
            else
            {
                Console.WriteLine("[chunk] source=" + source + " file=" + wavPath);
            }
        }

        /// <summary>95th percentile of 30 ms frame RMS: robust to short clicks, low for silence.</summary>
        /// <summary>
        /// Cuts the buffered audio at a pause between ChunkSeconds and MaxChunkSeconds, so words and
        /// phrases are not split at a fixed 5 s border (the model then guesses the cut word, often wrong).
        /// Without a pause the chunk is cut at the quietest moment once MaxChunkSeconds is reached.
        /// </summary>
        private static float[] TakeChunkAtPause(ChunkState state, RuntimeOptions options)
        {
            int rate = state.SampleRate;
            int minSamples = rate * options.ChunkSeconds;
            int maxSamples = rate * Math.Max(options.ChunkSeconds, options.MaxChunkSeconds);
            if (state.Samples.Count < minSamples)
            {
                return null;
            }

            int frameSamples = Math.Max(1, rate / 50); // 20 ms
            int available = Math.Min(state.Samples.Count, maxSamples);
            int frames = available / frameSamples;
            var levels = new double[frames];
            for (int frame = 0; frame < frames; frame++)
            {
                double sum = 0;
                int offset = frame * frameSamples;
                for (int i = 0; i < frameSamples; i++)
                {
                    float sample = state.Samples[offset + i];
                    sum += sample * sample;
                }

                levels[frame] = Math.Sqrt(sum / frameSamples);
            }

            // A pause is relative to the speech level: mic noise floors differ a lot between headsets.
            var sorted = (double[])levels.Clone();
            Array.Sort(sorted);
            double speechLevel = sorted.Length > 0 ? sorted[(int)(sorted.Length * 0.9)] : 0;
            double pauseLevel = Math.Max(options.SilenceThreshold, speechLevel * 0.15);

            const int PauseFrames = 15; // 300 ms
            int minFrame = minSamples / frameSamples;
            int quiet = 0;
            for (int frame = 0; frame < frames; frame++)
            {
                quiet = levels[frame] < pauseLevel ? quiet + 1 : 0;
                if (quiet >= PauseFrames && frame >= minFrame)
                {
                    return CutChunk(state, (frame - PauseFrames / 2) * frameSamples);
                }
            }

            if (state.Samples.Count < maxSamples)
            {
                return null;
            }

            int bestFrame = frames;
            double bestSum = double.MaxValue;
            for (int frame = Math.Max(minFrame, PauseFrames); frame <= frames; frame++)
            {
                double sum = 0;
                for (int i = frame - PauseFrames; i < frame; i++)
                {
                    sum += levels[i];
                }

                if (sum < bestSum)
                {
                    bestSum = sum;
                    bestFrame = frame - PauseFrames / 2;
                }
            }

            return CutChunk(state, bestFrame * frameSamples);
        }

        private static float[] CutChunk(ChunkState state, int length)
        {
            length = Math.Max(1, Math.Min(length, state.Samples.Count));
            float[] chunk = state.Samples.GetRange(0, length).ToArray();
            state.Samples.RemoveRange(0, length);
            return chunk;
        }

        private static double SpeechLevel(float[] samples)
        {
            int frames = samples.Length / VadFrameSamples;
            if (frames == 0)
            {
                return 0;
            }

            var levels = new double[frames];
            for (int frame = 0; frame < frames; frame++)
            {
                double sum = 0;
                int offset = frame * VadFrameSamples;
                for (int i = 0; i < VadFrameSamples; i++)
                {
                    float sample = samples[offset + i];
                    sum += sample * sample;
                }

                levels[frame] = Math.Sqrt(sum / VadFrameSamples);
            }

            Array.Sort(levels);
            return levels[(int)(frames * 0.95)];
        }

        private static void SttWorkerLoop(BlockingCollection<SttJob> jobs, RuntimeOptions options)
        {
            var lastTextBySource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var job in jobs.GetConsumingEnumerable())
            {
                try
                {
                    double lagSeconds = (DateTime.UtcNow - job.CreatedAtUtc).TotalSeconds;
                    Metrics.LastLagSeconds = lagSeconds;

                    // Real-time hints are useless minutes late: skip stale chunks so the pipeline catches up.
                    if (options.MaxSttLagSeconds > 0 && lagSeconds > options.MaxSttLagSeconds)
                    {
                        Interlocked.Increment(ref Metrics.DroppedChunks);
                        Console.WriteLine("[stt-drop] source=" + job.Source + " stale chunk skipped, lag=" + lagSeconds.ToString("0") + "s");
                        continue;
                    }

                    lastTextBySource.TryGetValue(job.Source, out string previousText);
                    string transcript = Transcribe(job, options, previousText);
                    // On silence or music the model sometimes returns only symbols ("❤", "..."): no letters, no speech.
                    if (string.IsNullOrWhiteSpace(transcript) || !transcript.Any(char.IsLetterOrDigit))
                    {
                        continue;
                    }

                    lastTextBySource[job.Source] = transcript;
                    Console.WriteLine("[stt][" + job.Source + "] " + transcript.Replace('\r', ' ').Replace('\n', ' '));
                }
                catch (Exception ex)
                {
                    if (options.SttErrorCounter != null)
                    {
                        options.SttErrorCounter();
                    }
                    Console.WriteLine("[stt-error][" + job.Source + "] " + ex.Message);
                }
                finally
                {
                    DeleteChunkFiles(job.WavPath, options);
                }
            }
        }

        private static string Transcribe(SttJob job, RuntimeOptions options, string previousText)
        {
            if (!options.UseApiStt)
            {
                return TranscribeLocally(job, options, previousText);
            }

            try
            {
                return TranscribeViaApi(job.WavPath, options, previousText);
            }
            catch (Exception ex) when (options.WhisperEnabled)
            {
                Console.WriteLine("[stt-fallback][" + job.Source + "] API failed, using local whisper: " + ex.Message);
                return TranscribeLocally(job, options, previousText);
            }
        }

        private static string TranscribeLocally(SttJob job, RuntimeOptions options, string previousText)
        {
            string serverUrl = WhisperServerUrl;
            if (serverUrl != null)
            {
                try
                {
                    return TranscribeViaWhisperServer(serverUrl, job.WavPath, options, previousText);
                }
                catch (Exception ex)
                {
                    if (WhisperServerProcess == null || WhisperServerProcess.HasExited)
                    {
                        WhisperServerUrl = null;
                    }

                    Console.WriteLine("[stt-fallback][" + job.Source + "] whisper-server failed, using whisper-cli: " + ex.Message);
                }
            }

            return RunWhisperCpp(job.WavPath, options);
        }

        /// <summary>
        /// whisper-server keeps the model in memory (whisper-cli reloads it for every chunk) and takes the
        /// prompt as UTF-8 over HTTP, so the Russian priming phrase and the glossary reach the model intact.
        /// </summary>
        private static void StartWhisperServer(RuntimeOptions options)
        {
            string serverPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.WhisperBinPath)), OperatingSystem.IsWindows() ? "whisper-server.exe" : "whisper-server");
            if (!File.Exists(serverPath))
            {
                return;
            }

            int port;
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            int threads = Math.Max(4, Math.Min(8, Environment.ProcessorCount / 2));
            var process = new Process();
            process.StartInfo.FileName = serverPath;
            process.StartInfo.Arguments = string.Format(
                "-m \"{0}\" -l {1} -t {2} -nt -sns --host 127.0.0.1 --port {3}",
                options.WhisperModelPath,
                options.Language,
                threads,
                port);
            process.StartInfo.WorkingDirectory = Path.GetDirectoryName(serverPath);
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            process.OutputDataReceived += (_, __) => { };
            process.ErrorDataReceived += (_, __) => { };

            try
            {
                process.Start();
                TieToThisProcess(process);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                AppDomain.CurrentDomain.ProcessExit += (_, __) => StopWhisperServer();

                // The server opens the port only after the model is loaded.
                string url = "http://127.0.0.1:" + port;
                DateTime deadline = DateTime.UtcNow.AddSeconds(60);
                while (DateTime.UtcNow < deadline && !process.HasExited)
                {
                    try
                    {
                        using (var probe = new System.Net.Sockets.TcpClient())
                        {
                            probe.Connect(IPAddress.Loopback, port);
                        }

                        WhisperServerProcess = process;
                        WhisperServerUrl = url;
                        return;
                    }
                    catch (System.Net.Sockets.SocketException)
                    {
                        Thread.Sleep(250);
                    }
                }

                Console.WriteLine("[stt-error] whisper-server did not start, falling back to whisper-cli.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[stt-error] whisper-server failed to start: " + ex.Message);
            }

            WhisperServerProcess = process;
            StopWhisperServer();
        }

        private static void StopWhisperServer()
        {
            Process process = WhisperServerProcess;
            WhisperServerUrl = null;
            WhisperServerProcess = null;
            try
            {
                if (process != null && !process.HasExited)
                {
                    process.Kill();
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// ProcessExit never runs when DesktopCapture is killed (installer, Task Manager, a crash), and an orphaned
        /// whisper-server keeps the model in memory and ggml-*.dll locked. A job that kills its processes when its
        /// last handle closes ends whisper-server together with DesktopCapture, however DesktopCapture ends.
        /// </summary>
        private static void TieToThisProcess(Process child)
        {
            // Job objects are Windows-only; on macOS the cuepilot-audio helpers and whisper-server are stopped by ProcessExit.
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            try
            {
                if (ChildProcessJob == IntPtr.Zero)
                {
                    IntPtr job = CreateJobObject(IntPtr.Zero, null);
                    if (job == IntPtr.Zero)
                    {
                        throw new InvalidOperationException("CreateJobObject failed: " + Marshal.GetLastWin32Error());
                    }

                    var info = new JobObjectExtendedLimitInformation();
                    info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
                    int length = Marshal.SizeOf(typeof(JobObjectExtendedLimitInformation));
                    IntPtr buffer = Marshal.AllocHGlobal(length);
                    try
                    {
                        Marshal.StructureToPtr(info, buffer, false);
                        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, buffer, (uint)length))
                        {
                            int error = Marshal.GetLastWin32Error();
                            CloseHandle(job);
                            throw new InvalidOperationException("SetInformationJobObject failed: " + error);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }

                    ChildProcessJob = job;
                }

                if (!AssignProcessToJobObject(ChildProcessJob, child.Handle))
                {
                    throw new InvalidOperationException("AssignProcessToJobObject failed: " + Marshal.GetLastWin32Error());
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[stt-warn] whisper-server is not tied to DesktopCapture and may outlive a kill: " + ex.Message);
            }
        }

        private const int JobObjectExtendedLimitInformationClass = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;

        // Never closed: the handle goes away with DesktopCapture, and that is what ends the job's processes.
        private static IntPtr ChildProcessJob = IntPtr.Zero;

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private static string TranscribeViaWhisperServer(string serverUrl, string wavPath, RuntimeOptions options, string previousText)
        {
            using (var content = new MultipartFormDataContent())
            {
                var file = new ByteArrayContent(File.ReadAllBytes(wavPath));
                file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                content.Add(file, "file", Path.GetFileName(wavPath));
                content.Add(new StringContent("0.0"), "temperature");
                content.Add(new StringContent("json"), "response_format");
                content.Add(new StringContent(options.Language), "language");
                string prompt = BuildContinuityPrompt("whisper.cpp", options, previousText);
                if (prompt.Length > 0)
                {
                    content.Add(new StringContent(prompt, Encoding.UTF8), "prompt");
                }

                using (HttpResponseMessage response = LocalWhisperHttpClient.PostAsync(serverUrl + "/inference", content).GetAwaiter().GetResult())
                {
                    string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException("whisper-server " + (int)response.StatusCode + ": " + Truncate(body, 300));
                    }

                    return ParseTranscriptionResponse(body);
                }
            }
        }

        private static string TranscribeViaApi(string wavPath, RuntimeOptions options, string previousText)
        {
            string url = options.SttApiUrl.TrimEnd('/') + "/audio/transcriptions";
            byte[] audio = File.ReadAllBytes(wavPath);
            string prompt = BuildContinuityPrompt(options.SttApiModel, options, previousText);

            for (int attempt = 1; ; attempt++)
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.SttApiKey);
                    request.Content = SttApiUsesJsonBody
                        ? BuildJsonTranscriptionBody(audio, options, prompt)
                        : BuildMultipartTranscriptionBody(audio, Path.GetFileName(wavPath), options, prompt);

                    using (HttpResponseMessage response = SttHttpClient.Send(request))
                    using (var reader = new StreamReader(response.Content.ReadAsStream(), Encoding.UTF8))
                    {
                        string body = reader.ReadToEnd();
                        int status = (int)response.StatusCode;
                        if (response.IsSuccessStatusCode)
                        {
                            return ParseTranscriptionResponse(body);
                        }

                        // Some gateways accept only one body format: switch once and retry.
                        if (attempt == 1 && (status == 400 || status == 415 || status == 422))
                        {
                            SttApiUsesJsonBody = !SttApiUsesJsonBody;
                            continue;
                        }

                        if (attempt <= 2 && (status == 408 || status == 429 || status >= 500))
                        {
                            Thread.Sleep(800 * attempt);
                            continue;
                        }

                        throw new InvalidOperationException("STT API " + status + ": " + Truncate(body, 300));
                    }
                }
            }
        }

        private static HttpContent BuildMultipartTranscriptionBody(byte[] audio, string fileName, RuntimeOptions options, string prompt)
        {
            var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(audio);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(fileContent, "file", fileName);
            content.Add(new StringContent(options.SttApiModel), "model");
            content.Add(new StringContent("json"), "response_format");
            if (!string.IsNullOrWhiteSpace(options.Language) && !string.Equals(options.Language, "auto", StringComparison.OrdinalIgnoreCase))
            {
                content.Add(new StringContent(options.Language), "language");
            }

            if (!string.IsNullOrWhiteSpace(prompt))
            {
                content.Add(new StringContent(prompt), "prompt");
            }

            return content;
        }

        private static HttpContent BuildJsonTranscriptionBody(byte[] audio, RuntimeOptions options, string prompt)
        {
            var payload = new Dictionary<string, object>
            {
                ["model"] = options.SttApiModel,
                ["file"] = "data:audio/wav;base64," + Convert.ToBase64String(audio),
                ["response_format"] = "json"
            };
            if (!string.IsNullOrWhiteSpace(options.Language) && !string.Equals(options.Language, "auto", StringComparison.OrdinalIgnoreCase))
            {
                payload["language"] = options.Language;
            }

            if (!string.IsNullOrWhiteSpace(prompt))
            {
                payload["prompt"] = prompt;
            }

            return new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        }

        private static string ParseTranscriptionResponse(string body)
        {
            string trimmed = (body ?? string.Empty).Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                return trimmed;
            }

            using (JsonDocument document = JsonDocument.Parse(trimmed))
            {
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("status", out JsonElement status) &&
                    status.ValueKind == JsonValueKind.String &&
                    string.Equals(status.GetString(), "processing", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("asynchronous STT models are not supported for live transcription");
                }

                if (root.TryGetProperty("usage", out JsonElement usage) &&
                    usage.ValueKind == JsonValueKind.Object &&
                    usage.TryGetProperty("cost_rub", out JsonElement cost) &&
                    cost.ValueKind == JsonValueKind.Number)
                {
                    Metrics.AddCost(cost.GetDouble());
                }

                return root.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String
                    ? (text.GetString() ?? string.Empty).Trim()
                    : string.Empty;
            }
        }

        /// <summary>Tail of the previous chunk helps the model keep words that were cut at the chunk border.</summary>
        /// <remarks>
        /// For Russian the prompt also starts with a Russian priming phrase: on short chunks the model
        /// drifts into Ukrainian spelling even with an explicit language. A previous chunk that already
        /// slipped into Ukrainian is not reused, so one bad chunk does not drag the next ones along.
        /// The glossary (names, terms) goes first so the model spells them as the user does.
        /// whisper-cli gets no prompt: its Windows build reads argv in the ANSI code page and would
        /// garble Cyrillic, so prompts reach local whisper only through whisper-server.
        /// </remarks>
        private static string BuildContinuityPrompt(string model, RuntimeOptions options, string previousText)
        {
            if (model.IndexOf("diarize", StringComparison.OrdinalIgnoreCase) >= 0 ||
                model.StartsWith("aiesa/", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            bool russian = string.Equals(options.Language, "ru", StringComparison.OrdinalIgnoreCase);
            string text = (previousText ?? string.Empty).Trim();
            if (russian && text.IndexOfAny(UkrainianOnlyLetters) >= 0)
            {
                text = string.Empty;
            }

            if (text.Length > 200)
            {
                text = text.Substring(text.Length - 200);
            }

            var parts = new List<string>();
            if (russian)
            {
                parts.Add(RussianPrimingPrompt);
            }

            if (!string.IsNullOrWhiteSpace(options.SttGlossary))
            {
                parts.Add((russian ? "Термины: " : "Terms: ") + options.SttGlossary.Trim() + ".");
            }

            parts.Add(text);
            return string.Join(" ", parts).Trim();
        }

        private const string RussianPrimingPrompt = "Разговор на русском языке.";
        private static readonly char[] UkrainianOnlyLetters = { 'і', 'ї', 'є', 'ґ', 'І', 'Ї', 'Є', 'Ґ' };

        private static string RunWhisperCpp(string wavPath, RuntimeOptions options)
        {
            string outPrefix = Path.Combine(
                Path.GetDirectoryName(wavPath),
                Path.GetFileNameWithoutExtension(wavPath) + "-whisper");

            // -nt: no timestamps, -sns: suppress non-speech tokens ([музыка], (аплодисменты) ...).
            string arguments = string.Format(
                "-m \"{0}\" -f \"{1}\" -l {2} -np -nt -sns -otxt -of \"{3}\"",
                options.WhisperModelPath,
                wavPath,
                options.Language,
                outPrefix);

            using (var process = new Process())
            {
                process.StartInfo.FileName = options.WhisperBinPath;
                process.StartInfo.Arguments = arguments;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                process.StartInfo.StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                process.StartInfo.CreateNoWindow = true;

                process.Start();
                var stderrTask = process.StandardError.ReadToEndAsync();
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = stderrTask.Result;
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        "whisper.cpp exit code " + process.ExitCode + ". stderr: " + Truncate(stderr, 300));
                }

                string txtPath = outPrefix + ".txt";
                if (File.Exists(txtPath))
                {
                    return File.ReadAllText(txtPath, Encoding.UTF8).Trim();
                }

                return (stdout ?? string.Empty).Trim();
            }
        }

        private static void DeleteChunkFiles(string wavPath, RuntimeOptions options)
        {
            if (options.KeepChunks || string.IsNullOrWhiteSpace(wavPath))
            {
                return;
            }

            TryDelete(wavPath);
            TryDelete(Path.Combine(
                Path.GetDirectoryName(wavPath),
                Path.GetFileNameWithoutExtension(wavPath) + "-whisper.txt"));
        }

        private static void DeleteLeftoverChunks(string chunksDirectory)
        {
            try
            {
                foreach (string path in Directory.EnumerateFiles(chunksDirectory, "*-system-*.*")
                    .Concat(Directory.EnumerateFiles(chunksDirectory, "*-mic-*.*")))
                {
                    if (File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddHours(-12))
                    {
                        TryDelete(path);
                    }
                }
            }
            catch (Exception)
            {
                // Cleanup is best effort.
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // A locked file is removed on the next run.
            }
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value ?? string.Empty;
            }

            return value.Substring(0, maxLength) + "...";
        }

        private static RuntimeOptions ParseOptions(string[] args)
        {
            var options = new RuntimeOptions
            {
                ChunkSeconds = 5,
                MaxChunkSeconds = 10,
                Language = "ru",
                SttChunksDirectory = Path.Combine(".", "stt-chunks"),
                SoakMinutes = 0,
                SilenceThreshold = 0.004,
                MaxSttLagSeconds = 60
            };

            int i = 0;
            while (i < args.Length)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "--whisper-bin":
                        i++;
                        options.WhisperBinPath = i < args.Length ? args[i] : null;
                        break;
                    case "--whisper-model":
                        i++;
                        options.WhisperModelPath = i < args.Length ? args[i] : null;
                        break;
                    case "--chunk-seconds":
                        i++;
                        if (i < args.Length && int.TryParse(args[i], out int seconds) && seconds > 0)
                        {
                            options.ChunkSeconds = seconds;
                        }
                        break;
                    case "--max-chunk-seconds":
                        i++;
                        if (i < args.Length && int.TryParse(args[i], out int maxSeconds) && maxSeconds > 0)
                        {
                            options.MaxChunkSeconds = maxSeconds;
                        }
                        break;
                    case "--stt-glossary":
                        i++;
                        string glossary = i < args.Length ? args[i].Trim() : string.Empty;
                        options.SttGlossary = glossary.Length <= 300 ? glossary : glossary.Substring(0, 300);
                        break;
                    case "--lang":
                        i++;
                        // Auto-detect flips between Russian and Ukrainian on short chunks, so it is not supported.
                        options.Language = i < args.Length && !string.IsNullOrWhiteSpace(args[i]) &&
                            !string.Equals(args[i], "auto", StringComparison.OrdinalIgnoreCase)
                            ? args[i]
                            : "ru";
                        break;
                    case "--stt-dir":
                        i++;
                        options.SttChunksDirectory = i < args.Length ? args[i] : Path.Combine(".", "stt-chunks");
                        break;
                    case "--soak-minutes":
                        i++;
                        if (i < args.Length && int.TryParse(args[i], out int soakMinutes) && soakMinutes > 0)
                        {
                            options.SoakMinutes = soakMinutes;
                        }
                        break;
                    case "--soak-report":
                        i++;
                        options.SoakReportPath = i < args.Length ? args[i] : null;
                        break;
                    case "--silence-threshold":
                        i++;
                        if (i < args.Length && double.TryParse(args[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double threshold) && threshold >= 0)
                        {
                            options.SilenceThreshold = threshold;
                        }
                        break;
                    case "--max-stt-lag":
                        i++;
                        if (i < args.Length && int.TryParse(args[i], out int maxLag) && maxLag >= 0)
                        {
                            options.MaxSttLagSeconds = maxLag;
                        }
                        break;
                    case "--keep-chunks":
                        options.KeepChunks = true;
                        break;
                    case "--stt-provider":
                        i++;
                        options.UseApiStt = i < args.Length && string.Equals(args[i], "api", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "--stt-api-url":
                        i++;
                        options.SttApiUrl = i < args.Length ? args[i] : null;
                        break;
                    case "--stt-api-model":
                        i++;
                        options.SttApiModel = i < args.Length ? args[i] : null;
                        break;
                    case "--transcribe-file":
                        i++;
                        options.TranscribeFilePath = i < args.Length ? args[i] : null;
                        break;
                    default:
                        if (!arg.StartsWith("--") && string.IsNullOrWhiteSpace(options.OutputPath))
                        {
                            options.OutputPath = arg;
                        }
                        break;
                }

                i++;
            }

            if (!string.IsNullOrWhiteSpace(options.WhisperBinPath) || !string.IsNullOrWhiteSpace(options.WhisperModelPath))
            {
                if (string.IsNullOrWhiteSpace(options.WhisperBinPath) || string.IsNullOrWhiteSpace(options.WhisperModelPath))
                {
                    throw new ArgumentException("Both --whisper-bin and --whisper-model are required together.");
                }
            }

            return options;
        }

        private static float[] DecodeToMonoFloat(byte[] buffer, int bytesRecorded, WaveFormat format)
        {
            int channels = Math.Max(1, format.Channels);
            int bitsPerSample = format.BitsPerSample;
            int bytesPerSample = bitsPerSample / 8;
            if (bytesPerSample <= 0)
            {
                return Array.Empty<float>();
            }

            int frameSize = bytesPerSample * channels;
            if (frameSize <= 0)
            {
                return Array.Empty<float>();
            }

            int frames = bytesRecorded / frameSize;
            var result = new float[frames];

            for (int frame = 0; frame < frames; frame++)
            {
                float sum = 0f;
                int frameOffset = frame * frameSize;
                for (int channel = 0; channel < channels; channel++)
                {
                    int sampleOffset = frameOffset + (channel * bytesPerSample);
                    sum += ReadSampleAsFloat(buffer, sampleOffset, bitsPerSample, format.Encoding);
                }

                result[frame] = sum / channels;
            }

            return result;
        }

        private static float ReadSampleAsFloat(byte[] buffer, int offset, int bitsPerSample, WaveFormatEncoding encoding)
        {
            if (encoding == WaveFormatEncoding.IeeeFloat && bitsPerSample == 32)
            {
                return BitConverter.ToSingle(buffer, offset);
            }

            if (bitsPerSample == 16)
            {
                return BitConverter.ToInt16(buffer, offset) / 32768f;
            }

            if (bitsPerSample == 24)
            {
                int value = (buffer[offset + 2] << 16) | (buffer[offset + 1] << 8) | buffer[offset];
                if ((value & 0x800000) != 0)
                {
                    value |= unchecked((int)0xFF000000);
                }

                return value / 8388608f;
            }

            if (bitsPerSample == 32)
            {
                return BitConverter.ToInt32(buffer, offset) / 2147483648f;
            }

            return 0f;
        }

        private static float FindPeak(float[] samples)
        {
            float peak = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                float abs = Math.Abs(samples[i]);
                if (abs > peak)
                {
                    peak = abs;
                }
            }

            return peak;
        }

        private static float[] ResampleTo16000(float[] source, int sourceRate)
        {
            if (source == null || source.Length == 0 || sourceRate <= 0)
            {
                return Array.Empty<float>();
            }

            if (sourceRate == WhisperTargetSampleRate)
            {
                return source;
            }

            double ratio = (double)WhisperTargetSampleRate / sourceRate;
            int targetLength = (int)Math.Max(1, Math.Round(source.Length * ratio));
            var target = new float[targetLength];

            for (int i = 0; i < targetLength; i++)
            {
                double sourcePos = i / ratio;
                int left = (int)Math.Floor(sourcePos);
                int right = Math.Min(left + 1, source.Length - 1);
                double frac = sourcePos - left;
                double interpolated = (source[left] * (1.0 - frac)) + (source[right] * frac);
                target[i] = ClampSample((float)interpolated);
            }

            return target;
        }

        private static void WriteMono16kWav(string path, float[] samples)
        {
            var format = new WaveFormat(WhisperTargetSampleRate, 16, 1);
            using (var writer = new WaveFileWriter(path, format))
            {
                var buffer = new byte[samples.Length * 2];
                int offset = 0;
                for (int i = 0; i < samples.Length; i++)
                {
                    short sample = (short)Math.Round(ClampSample(samples[i]) * short.MaxValue);
                    var bytes = BitConverter.GetBytes(sample);
                    buffer[offset++] = bytes[0];
                    buffer[offset++] = bytes[1];
                }

                writer.Write(buffer, 0, buffer.Length);
            }
        }

        private static float ClampSample(float value)
        {
            if (value > 1f)
            {
                return 1f;
            }

            if (value < -1f)
            {
                return -1f;
            }

            return value;
        }

        private static string BuildMicPath(string systemPath)
        {
            string directory = Path.GetDirectoryName(systemPath);
            string name = Path.GetFileNameWithoutExtension(systemPath);
            string ext = Path.GetExtension(systemPath);
            if (string.IsNullOrWhiteSpace(ext))
            {
                ext = ".wav";
            }

            return Path.Combine(directory, name + "-mic" + ext);
        }

        private static void WriteSoakReport(
            RuntimeOptions options,
            DateTime startedAtUtc,
            DateTime endedAtUtc,
            bool autoStop,
            bool manualStop,
            bool hadSystemCaptureError,
            bool hadMicCaptureError,
            bool hadSttError,
            bool sttWorkerFinished,
            bool deviceCheckEvaluated,
            bool deviceCheckPass,
            AudioEndpointSnapshot before,
            AudioEndpointSnapshot after,
            int systemChunks,
            int micChunks,
            bool soakDurationReached,
            bool soakPass)
        {
            var builder = new StringBuilder();
            builder.AppendLine("{");
            builder.AppendLine("  \"version\": 1,");
            builder.AppendLine("  \"mode\": \"desktop-capture-soak\",");
            builder.AppendLine("  \"startedAtUtc\": \"" + startedAtUtc.ToString("o") + "\",");
            builder.AppendLine("  \"endedAtUtc\": \"" + endedAtUtc.ToString("o") + "\",");
            builder.AppendLine("  \"durationSeconds\": " + ((endedAtUtc - startedAtUtc).TotalSeconds).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + ",");
            builder.AppendLine("  \"soak\": {");
            builder.AppendLine("    \"enabled\": " + (options.SoakMinutes > 0 ? "true" : "false") + ",");
            builder.AppendLine("    \"targetMinutes\": " + options.SoakMinutes + ",");
            builder.AppendLine("    \"durationReached\": " + (soakDurationReached ? "true" : "false") + ",");
            builder.AppendLine("    \"autoStop\": " + (autoStop ? "true" : "false") + ",");
            builder.AppendLine("    \"manualStop\": " + (manualStop ? "true" : "false") + ",");
            builder.AppendLine("    \"result\": \"" + (soakPass ? "PASS" : "FAIL") + "\"");
            builder.AppendLine("  },");
            builder.AppendLine("  \"capture\": {");
            builder.AppendLine("    \"chunkSeconds\": " + options.ChunkSeconds + ",");
            builder.AppendLine("    \"systemChunks\": " + systemChunks + ",");
            builder.AppendLine("    \"micChunks\": " + micChunks + ",");
            builder.AppendLine("    \"hadSystemCaptureError\": " + (hadSystemCaptureError ? "true" : "false") + ",");
            builder.AppendLine("    \"hadMicCaptureError\": " + (hadMicCaptureError ? "true" : "false"));
            builder.AppendLine("  },");
            builder.AppendLine("  \"stt\": {");
            builder.AppendLine("    \"enabled\": " + (options.WhisperEnabled ? "true" : "false") + ",");
            builder.AppendLine("    \"hadSttError\": " + (hadSttError ? "true" : "false") + ",");
            builder.AppendLine("    \"workerFinished\": " + (sttWorkerFinished ? "true" : "false") + ",");
            builder.AppendLine("    \"language\": \"" + EscapeJson(options.Language) + "\"");
            builder.AppendLine("  },");
            builder.AppendLine("  \"deviceCheck\": {");
            builder.AppendLine("    \"evaluated\": " + (deviceCheckEvaluated ? "true" : "false") + ",");
            builder.AppendLine("    \"pass\": " + (deviceCheckPass ? "true" : "false") + ",");
            builder.AppendLine("    \"before\": " + SnapshotToJson(before) + ",");
            builder.AppendLine("    \"after\": " + SnapshotToJson(after));
            builder.AppendLine("  }");
            builder.AppendLine("}");

            File.WriteAllText(options.SoakReportPath, builder.ToString(), Encoding.UTF8);
            Console.WriteLine("[soak] report saved: " + options.SoakReportPath);
        }

        private static string SnapshotToJson(AudioEndpointSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return "null";
            }

            var builder = new StringBuilder();
            builder.Append("{");
            builder.Append("\"deviceId\":\"").Append(EscapeJson(snapshot.DeviceId)).Append("\",");
            builder.Append("\"deviceName\":\"").Append(EscapeJson(snapshot.DeviceName)).Append("\",");
            builder.Append("\"volumePercent\":").Append(snapshot.MasterVolumePercent.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)).Append(",");
            builder.Append("\"muted\":").Append(snapshot.IsMuted ? "true" : "false").Append(",");
            builder.Append("\"sampleRate\":").Append(snapshot.SampleRate).Append(",");
            builder.Append("\"channels\":").Append(snapshot.Channels).Append(",");
            builder.Append("\"bitsPerSample\":").Append(snapshot.BitsPerSample);
            builder.Append("}");
            return builder.ToString();
        }

        private static string EscapeJson(string value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
        }

        private class StreamStats
        {
            public long TotalBytes;
            public float Peak;
        }

        private class ChunkState
        {
            public ChunkState(WaveFormat format)
            {
                SampleRate = format.SampleRate;
                Samples = new List<float>(format.SampleRate * 10);
            }

            public int SampleRate;
            public List<float> Samples;
        }

        private class SttJob
        {
            public string Source;
            public string WavPath;
            public DateTime CreatedAtUtc;
        }

        private class SttMetrics
        {
            public int SilentChunks;
            public int DroppedChunks;
            public volatile int LastLagMilliseconds;
            private long _costMicroRub;

            public double LastLagSeconds
            {
                set { LastLagMilliseconds = (int)Math.Min(int.MaxValue, value * 1000); }
            }

            public double CostRub
            {
                get { return Interlocked.Read(ref _costMicroRub) / 1_000_000d; }
            }

            public void AddCost(double rub)
            {
                Interlocked.Add(ref _costMicroRub, (long)(rub * 1_000_000));
            }

            /// <summary>Age of the oldest queued chunk, or the lag of the last processed one when the queue is empty.</summary>
            public double CurrentLagSeconds(BlockingCollection<SttJob> first, BlockingCollection<SttJob> second)
            {
                DateTime oldest = DateTime.MaxValue;
                foreach (SttJob job in first.ToArray().Concat(ReferenceEquals(first, second) ? Array.Empty<SttJob>() : second.ToArray()))
                {
                    if (job.CreatedAtUtc < oldest)
                    {
                        oldest = job.CreatedAtUtc;
                    }
                }

                return oldest == DateTime.MaxValue
                    ? LastLagMilliseconds / 1000d
                    : (DateTime.UtcNow - oldest).TotalSeconds;
            }
        }

        private class RuntimeOptions
        {
            public string OutputPath;
            public string WhisperBinPath;
            public string WhisperModelPath;
            public int ChunkSeconds;
            public int MaxChunkSeconds;
            public string Language;
            public string SttChunksDirectory;
            public int SoakMinutes;
            public string SoakReportPath;
            public Action SttErrorCounter;
            public double SilenceThreshold;
            public int MaxSttLagSeconds;
            public bool KeepChunks;
            public bool UseApiStt;
            public string SttApiUrl;
            public string SttApiModel;
            public string SttApiKey;
            public string SttGlossary;

            /// <summary>Audio or video file to transcribe instead of recording (see FileTranscription.cs).</summary>
            public string TranscribeFilePath;

            public bool WhisperEnabled
            {
                get
                {
                    return !string.IsNullOrWhiteSpace(WhisperBinPath)
                        && !string.IsNullOrWhiteSpace(WhisperModelPath);
                }
            }
        }

        // Filled by From() on Windows only, so the macOS build would warn that the fields are never assigned.
#pragma warning disable CS0649
        internal class AudioEndpointSnapshot
        {
            public string DeviceId;
            public string DeviceName;
            public float MasterVolumePercent;
            public bool IsMuted;
            public int SampleRate;
            public int Channels;
            public int BitsPerSample;
#pragma warning restore CS0649

#if WINDOWS
            public static AudioEndpointSnapshot From(MMDevice device)
            {
                var mix = device.AudioClient.MixFormat;
                return new AudioEndpointSnapshot
                {
                    DeviceId = device.ID,
                    DeviceName = device.FriendlyName,
                    MasterVolumePercent = device.AudioEndpointVolume.MasterVolumeLevelScalar * 100f,
                    IsMuted = device.AudioEndpointVolume.Mute,
                    SampleRate = mix.SampleRate,
                    Channels = mix.Channels,
                    BitsPerSample = mix.BitsPerSample
                };
            }
#endif
        }
    }
}
