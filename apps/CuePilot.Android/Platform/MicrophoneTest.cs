using System.Diagnostics;
using CuePilotDesktop.Services;

namespace CuePilot.Android.Platform;

/// <summary>
/// «Проверить микрофон и распознавание» in Settings: records a few seconds, measures the level, then recognizes
/// them with the engine a recording would use and says, in words, what works and what does not.
/// </summary>
public static class MicrophoneTest
{
    public const int Seconds = 5;
    private const int SampleRate = 16000;

    /// <summary>Runs off the UI thread; <paramref name="level"/> gets 0…1 every 0.1 s while recording.</summary>
    public static Task<string> RunAsync(CaptureStartOptions options, Action<double> level, Action recognizing, CancellationToken token) =>
        Task.Run(() => Run(options, level, recognizing, token), CancellationToken.None);

    private static string Run(CaptureStartOptions options, Action<double> level, Action recognizing, CancellationToken token)
    {
        var report = new List<string>();
        short[] samples;
        try
        {
            samples = Record(level, token);
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
        catch (Exception ex)
        {
            return string.Format(MobileText.Get("TestMicFailed"), ex.Message);
        }

        (double peak, double voicedShare) = Measure(samples);
        string percent = (peak * 100).ToString("0.#");
        bool silent = peak < 0.003;
        report.Add(silent
            ? MobileText.Get("TestMicSilent")
            : string.Format(MobileText.Get(peak < 0.03 ? "TestMicQuiet" : "TestMicOk"), percent));

        ISpeechRecognizer? speech = CaptureOrchestratorService.CreateRecognizer(options);
        if (speech == null)
        {
            report.Add(MobileText.Get("TestNoEngine"));
            return string.Join("\n\n", report);
        }

        if (silent)
        {
            speech.Dispose();
            return string.Join("\n\n", report);
        }

        recognizing();
        var clock = Stopwatch.StartNew();
        try
        {
            using (speech)
            {
                string text = speech.Transcribe(CaptureOrchestratorService.Normalize(samples), string.Empty, token);
                double took = clock.Elapsed.TotalSeconds;
                bool recognized = !string.IsNullOrWhiteSpace(text) && text.Any(char.IsLetterOrDigit) && !speech.IsPromptEcho(text);
                report.Add(recognized
                    ? string.Format(MobileText.Get("TestRecognized"), speech.Name, took, text.Trim())
                    : string.Format(MobileText.Get(voicedShare < 0.1 ? "TestNothingSaid" : "TestNotRecognized"), speech.Name, took));
                if (!options.UseApiStt && took > Seconds)
                {
                    report.Add(MobileText.Get("TestSlow"));
                }
            }
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
        catch (Exception ex)
        {
            report.Add(string.Format(MobileText.Get("TestRecognitionFailed"), speech.Name, ex.Message));
        }

        return string.Join("\n\n", report);
    }

    private static short[] Record(Action<double> level, CancellationToken token)
    {
        IPcmSource source = CaptureOrchestratorService.PcmSourceFactory?.Invoke()
            ?? throw new InvalidOperationException("Microphone is not available.");
        var all = new List<short>(SampleRate * Seconds);
        var frame = new short[SampleRate / 10];
        try
        {
            source.Start();
            while (all.Count < SampleRate * Seconds)
            {
                token.ThrowIfCancellationRequested();
                int read = source.Read(frame);
                if (read < 0)
                {
                    throw new InvalidOperationException("microphone read error " + read);
                }

                int peak = 0;
                for (int i = 0; i < read; i++)
                {
                    all.Add(frame[i]);
                    peak = Math.Max(peak, Math.Abs((int)frame[i]));
                }

                level(Math.Clamp(Math.Sqrt(peak / 32768d) * 1.4, 0, 1));
            }
        }
        finally
        {
            source.Stop();
            source.Dispose();
        }

        return all.ToArray();
    }

    /// <summary>Peak 0…1 and the share of 0.1 s frames above the speech level.</summary>
    private static (double Peak, double VoicedShare) Measure(short[] samples)
    {
        int frames = 0;
        int voiced = 0;
        double peak = 0;
        for (int start = 0; start + (SampleRate / 10) <= samples.Length; start += SampleRate / 10)
        {
            double sum = 0;
            for (int i = start; i < start + (SampleRate / 10); i++)
            {
                double value = samples[i] / 32768d;
                sum += value * value;
                peak = Math.Max(peak, Math.Abs(value));
            }

            frames++;
            if (Math.Sqrt(sum / (SampleRate / 10)) >= CaptureOrchestratorService.MinSpeechRms * 2)
            {
                voiced++;
            }
        }

        return (peak, frames == 0 ? 0 : voiced / (double)frames);
    }
}
