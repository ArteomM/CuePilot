using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CuePilotDesktop.Services;

/// <summary>
/// The speech API of the phone (OpenAI-compatible /audio/transcriptions), with the same prompt, retries and
/// body-format fallback as DesktopCapture. Used by live recording and by media files in the knowledge base.
/// </summary>
public sealed class SpeechApiClient : ISpeechRecognizer
{
    public const int SampleRate = 16000;
    private static readonly HttpClient SttHttpClient = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly string _apiUrl;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _language;
    private readonly string _glossary;
    private bool _useJsonBody;

    public SpeechApiClient(string apiUrl, string apiKey, string model, string language, string glossary)
    {
        _apiUrl = apiUrl ?? string.Empty;
        _apiKey = apiKey ?? string.Empty;
        _model = model ?? string.Empty;
        _language = language ?? string.Empty;
        _glossary = SpeechPrompt.NormalizeGlossary(glossary);
    }

    public string Name => "API " + _model;

    /// <summary>Total cost reported by the API (usage.cost_rub), RUB.</summary>
    public double CostRub { get; private set; }

    public string Transcribe(short[] samples, string previousText, CancellationToken token) =>
        Transcribe(BuildWav(samples), previousText, token);

    /// <summary>Recognizes one WAV chunk; <paramref name="previousText"/> keeps words cut at the chunk border.</summary>
    public string Transcribe(byte[] wav, string previousText, CancellationToken token)
    {
        string url = _apiUrl.TrimEnd('/') + "/audio/transcriptions";
        string prompt = SpeechPrompt.Build(_model, _language, _glossary, previousText);
        for (int attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            request.Content = _useJsonBody ? BuildJsonBody(wav, prompt) : BuildMultipartBody(wav, prompt);
            using HttpResponseMessage response = SttHttpClient.Send(request, token);
            using var reader = new StreamReader(response.Content.ReadAsStream(token), Encoding.UTF8);
            string body = reader.ReadToEnd();
            int status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                return ParseResponse(body);
            }

            // Some gateways accept only one body format: switch once and retry.
            if (attempt == 1 && status is 400 or 415 or 422)
            {
                _useJsonBody = !_useJsonBody;
                continue;
            }

            if (attempt <= 2 && (status is 408 or 429 || status >= 500))
            {
                token.WaitHandle.WaitOne(800 * attempt);
                token.ThrowIfCancellationRequested();
                continue;
            }

            throw new InvalidOperationException("STT API " + status + ": " + Truncate(body, 300));
        }
    }

    private HttpContent BuildMultipartBody(byte[] wav, string prompt)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(file, "file", "chunk.wav");
        content.Add(new StringContent(_model), "model");
        content.Add(new StringContent("json"), "response_format");
        if (HasLanguage)
        {
            content.Add(new StringContent(_language), "language");
        }

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            content.Add(new StringContent(prompt), "prompt");
        }

        return content;
    }

    private HttpContent BuildJsonBody(byte[] wav, string prompt)
    {
        var payload = new Dictionary<string, object>
        {
            ["model"] = _model,
            ["file"] = "data:audio/wav;base64," + Convert.ToBase64String(wav),
            ["response_format"] = "json"
        };
        if (HasLanguage)
        {
            payload["language"] = _language;
        }

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            payload["prompt"] = prompt;
        }

        return new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
    }

    private bool HasLanguage =>
        !string.IsNullOrWhiteSpace(_language) && !string.Equals(_language, "auto", StringComparison.OrdinalIgnoreCase);

    private string ParseResponse(string body)
    {
        string trimmed = (body ?? string.Empty).Trim();
        if (!trimmed.StartsWith('{'))
        {
            return trimmed;
        }

        using JsonDocument document = JsonDocument.Parse(trimmed);
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
            CostRub += cost.GetDouble();
        }

        return root.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String
            ? (text.GetString() ?? string.Empty).Trim()
            : string.Empty;
    }

    public bool IsPromptEcho(string text) => SpeechPrompt.IsEcho(text, _glossary);

    public void Dispose()
    {
    }

    /// <summary>16 kHz mono 16-bit WAV.</summary>
    public static byte[] BuildWav(short[] samples)
    {
        int dataBytes = samples.Length * 2;
        using var stream = new MemoryStream(44 + dataBytes);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        foreach (short sample in samples)
        {
            writer.Write(sample);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static string Truncate(string text, int length) =>
        string.IsNullOrEmpty(text) || text.Length <= length ? text : text[..length] + "…";
}
