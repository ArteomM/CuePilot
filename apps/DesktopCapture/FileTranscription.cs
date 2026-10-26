using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.Wave;

namespace DesktopCapture
{
    partial class Program
    {
        // Whisper reads 30 s windows, so file chunks are as long as that allows; live capture keeps them short for speed.
        private const int FileChunkSeconds = 15;
        private const int FileMaxChunkSeconds = 28;

        /// <summary>
        /// --transcribe-file: transcribes the sound of an audio or video file with the same engine as live capture
        /// (local whisper or the STT API) and prints, in order:
        /// <c>[file-start] duration=&lt;s&gt; method=&lt;decoder&gt;</c>, then per chunk <c>[file-stt] &lt;start&gt; &lt;end&gt; &lt;text&gt;</c>
        /// or <c>[file-stt-error] &lt;start&gt; &lt;message&gt;</c> and <c>[file-progress] &lt;done&gt; &lt;total&gt;</c> (seconds),
        /// finally <c>[file-done] chunks=&lt;n&gt; failed=&lt;n&gt; cost=&lt;rub&gt;</c>, or <c>[file-error] &lt;message&gt;</c>.
        /// A line on stdin (or stdin closing) stops after the current chunk. Exit code: 0 done, 2 stopped, 3+ failed.
        /// </summary>
        private static int TranscribeFile(RuntimeOptions options, string chunksDirectory)
        {
            if (!options.WhisperEnabled && !options.UseApiStt)
            {
                Console.WriteLine("[file-error] no speech recognition engine: pass --whisper-bin and --whisper-model, or --stt-provider api");
                return 3;
            }

            string path = Path.GetFullPath(options.TranscribeFilePath);
            if (!File.Exists(path))
            {
                Console.WriteLine("[file-error] file not found: " + path);
                return 3;
            }

            // Pauses inside 15–28 s; a chunk is cut at the quietest moment when there is no pause.
            options.ChunkSeconds = FileChunkSeconds;
            options.MaxChunkSeconds = FileMaxChunkSeconds;
            DeleteLeftoverFileChunks(chunksDirectory);

            int stopRequested = 0;
            new Thread(() =>
            {
                try
                {
                    Console.In.ReadLine();
                }
                catch
                {
                }

                Interlocked.Exchange(ref stopRequested, 1);
            }) { IsBackground = true }.Start();

            MediaDecoder decoder;
            try
            {
                decoder = MediaDecoder.Open(path, chunksDirectory);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[file-error] " + OneLine(ex.Message));
                return 4;
            }

            int transcribed = 0;
            int failed = 0;
            string lastError = null;
            using (decoder)
            {
                Console.WriteLine("[file-start] duration=" + Seconds(decoder.Duration.TotalSeconds) + " method=" + decoder.Method);
                var state = new ChunkState(new WaveFormat(MediaDecoder.SampleRate, 16, 1));
                var block = new float[MediaDecoder.SampleRate];
                long cutSamples = 0;
                int index = 0;
                string previousText = null;
                bool end = false;
                try
                {
                    while (!end && Volatile.Read(ref stopRequested) == 0)
                    {
                        int read = decoder.Read(block, 0, block.Length);
                        if (read > 0)
                        {
                            state.Samples.AddRange(new ArraySegment<float>(block, 0, read));
                        }
                        else
                        {
                            end = true;
                        }

                        while (Volatile.Read(ref stopRequested) == 0)
                        {
                            float[] chunk = TakeChunkAtPause(state, options) ??
                                (end && state.Samples.Count > 0 ? CutChunk(state, state.Samples.Count) : null);
                            if (chunk == null)
                            {
                                break;
                            }

                            double start = cutSamples / (double)MediaDecoder.SampleRate;
                            cutSamples += chunk.Length;
                            double finish = cutSamples / (double)MediaDecoder.SampleRate;
                            string text = TranscribeFileChunk(chunk, options, chunksDirectory, index++, start, finish, previousText, out string error);
                            if (error != null)
                            {
                                failed++;
                                lastError = error;
                                Console.WriteLine("[file-stt-error] " + Seconds(start) + " " + OneLine(error));
                            }
                            else if (text != null)
                            {
                                transcribed++;
                                previousText = text;
                                Console.WriteLine("[file-stt] " + Seconds(start) + " " + Seconds(finish) + " " + OneLine(text));
                            }

                            // Resampling can run a few samples past the length the container reports.
                            double total = decoder.Duration.TotalSeconds;
                            Console.WriteLine("[file-progress] " + Seconds(total > 0 ? Math.Min(finish, total) : finish) + " " + Seconds(total));

                            // Nothing gets through (no key, no money, the server is down): no point in going on.
                            if (transcribed == 0 && failed >= 5)
                            {
                                Console.WriteLine("[file-error] " + OneLine(lastError));
                                return 5;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[file-error] " + OneLine(ex.Message));
                    return 4;
                }
            }

            bool stopped = Volatile.Read(ref stopRequested) != 0;
            Console.WriteLine("[file-done] chunks=" + transcribed + " failed=" + failed +
                " cost=" + Metrics.CostRub.ToString("0.####", CultureInfo.InvariantCulture) + (stopped ? " stopped" : string.Empty));
            return stopped ? 2 : 0;
        }

        /// <summary>Text of one chunk; null for silence or a reply without words. Errors come back in <paramref name="error"/>.</summary>
        private static string TranscribeFileChunk(
            float[] chunk,
            RuntimeOptions options,
            string chunksDirectory,
            int index,
            double start,
            double finish,
            string previousText,
            out string error)
        {
            error = null;
            if (SpeechLevel(chunk) < options.SilenceThreshold)
            {
                Interlocked.Increment(ref Metrics.SilentChunks);
                return null;
            }

            string wavPath = Path.Combine(
                chunksDirectory,
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-file-" + Environment.ProcessId + "-" + index.ToString("D5") + ".wav");
            try
            {
                WriteMono16kWav(wavPath, chunk);
                var job = new SttJob { Source = "file", WavPath = wavPath, CreatedAtUtc = DateTime.UtcNow };
                string text = Transcribe(job, options, previousText);

                // On silence or music the model sometimes returns only symbols ("❤", "...") or repeats its prompt: no speech.
                return string.IsNullOrWhiteSpace(text) || !text.Any(char.IsLetterOrDigit) || IsPromptEcho(text, options) ? null : text.Trim();
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
            finally
            {
                DeleteChunkFiles(wavPath, options);
            }
        }

        /// <summary>Chunks of a transcription that was killed instead of stopped.</summary>
        private static void DeleteLeftoverFileChunks(string chunksDirectory)
        {
            try
            {
                foreach (string path in Directory.EnumerateFiles(chunksDirectory, "*-file-*.*"))
                {
                    if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddHours(-12))
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

        private static string Seconds(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        private static string OneLine(string value) => (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
    }
}
