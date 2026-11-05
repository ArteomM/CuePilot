using CuePilotDesktop.Services;

namespace CuePilot.Android.Platform;

/// <summary>
/// A check the CI smoke test starts on the emulator (an intent extra): one speech API request the way a recording
/// sends it, with a made-up key. Any HTTP answer (401 included) proves the request goes out and the answer is read;
/// on 2026-11-05 every API request failed on the phone because Android's HttpClient has no synchronous Send.
/// Never uses the user's key or settings.
/// </summary>
public static class SelfTest
{
    public const string SttExtra = "cuepilot_selftest_stt";

    public static void RunSpeechApi(string apiUrl)
    {
        if (!apiUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            PhonePlatform.Log("[selftest] Skipped: not an https address.");
            return;
        }

        PhonePlatform.Log("[selftest] Speech API request to " + apiUrl + " starts.");
        new Thread(() =>
        {
            var samples = new short[SpeechApiClient.SampleRate * 3 / 2];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = (short)(Math.Sin(2 * Math.PI * 440 * i / SpeechApiClient.SampleRate) * 8000);
            }

            // How the 1.0.5 builds sent it, for the record in the CI log.
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                using var response = http.Send(new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, apiUrl));
                PhonePlatform.Log("[selftest] Synchronous HttpClient.Send works: " + (int)response.StatusCode + ".");
            }
            catch (Exception ex)
            {
                PhonePlatform.Log("[selftest] Synchronous HttpClient.Send fails: " + ex.GetType().Name + ": " + ex.Message);
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            try
            {
                var client = new SpeechApiClient(apiUrl, "cuepilot-selftest", "openai/gpt-4o-mini-transcribe", "ru", string.Empty);
                client.Transcribe(samples, string.Empty, timeout.Token);
                PhonePlatform.Log("[selftest] STT API request OK: the server answered with text.");
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("STT API ", StringComparison.Ordinal))
            {
                PhonePlatform.Log("[selftest] STT API request OK: the server answered " + ex.Message[8..Math.Min(ex.Message.Length, 11)] + ".");
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException)
            {
                PhonePlatform.Log("[selftest] STT API network unavailable: " + ex.GetType().Name + ": " + ex.Message);
            }
            catch (Exception ex)
            {
                PhonePlatform.Log("[selftest] STT API FAILED: " + ex.GetType().Name + ": " + ex.Message);
            }
        }) { IsBackground = true, Name = "CuePilot self-test" }.Start();
    }
}
