using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CuePilotDesktop.Services;

public enum ModelKind
{
    Chat,
    Speech
}

/// <summary>A row of a Settings model dropdown: the id exactly as the provider spells it and a short note.</summary>
public sealed record ModelOption(string Id, string Note, bool IsRecommended);

/// <summary>A model as the provider's GET /models lists it; prices only when the provider publishes them.</summary>
public sealed record RemoteModel(string Id, string Name, decimal? PriceIn, decimal? PriceOut, string Currency);

/// <summary>
/// Model ids for the Settings model fields. A few known ids per provider are offered at once; the full list comes
/// from the provider's OpenAI-compatible GET /models (Polza.ai and OpenRouter answer without a key), so the
/// dropdown stays current when providers add or rename models.
/// </summary>
public static class ModelCatalog
{
    public const string SttCustomPreset = "Custom";

    /// <summary>Speech-to-text providers with an OpenAI-compatible /audio/transcriptions.</summary>
    public static readonly string[] SttPresets = [AppSettings.PolzaPresetName, "OpenAI", "Groq", SttCustomPreset];

    private static readonly Dictionary<string, (string BaseUrl, string Model)> SttPresetDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        [AppSettings.PolzaPresetName] = (AppSettings.PolzaBaseUrl, AppSettings.PolzaSttModel),
        ["OpenAI"] = ("https://api.openai.com/v1", "gpt-4o-mini-transcribe"),
        ["Groq"] = ("https://api.groq.com/openai/v1", "whisper-large-v3-turbo")
    };

    // Second item: the resx key of the short note shown next to the id.
    private static readonly Dictionary<string, (string Id, string NoteKey)[]> ChatSuggestions = new(StringComparer.OrdinalIgnoreCase)
    {
        [AppSettings.PolzaPresetName] = [(AppSettings.PolzaFastModel, "ModelNoteFast"), (AppSettings.PolzaAnalysisModel, "ModelNoteSmart")],
        ["OpenAI"] = [("gpt-5-mini", "ModelNoteFast"), ("gpt-4.1-mini", "ModelNoteFast"), ("gpt-5", "ModelNoteSmart")],
        ["OpenRouter"] = [(AppSettings.PolzaFastModel, "ModelNoteFast"), (AppSettings.PolzaAnalysisModel, "ModelNoteSmart")],
        ["Groq"] = [("llama-3.1-8b-instant", "ModelNoteFast"), ("llama-3.3-70b-versatile", "ModelNoteSmart"), ("openai/gpt-oss-120b", "ModelNoteSmart")],
        ["Together"] = [("meta-llama/Llama-3.3-70B-Instruct-Turbo", "ModelNoteSmart")],
        ["Ollama (OpenAI-compatible)"] = [("llama3.1:8b", "ModelNoteLocal"), ("qwen2.5:7b", "ModelNoteLocal")]
    };

    private static readonly Dictionary<string, (string Id, string NoteKey)[]> SpeechSuggestions = new(StringComparer.OrdinalIgnoreCase)
    {
        [AppSettings.PolzaPresetName] = [(AppSettings.PolzaSttModel, "ModelNoteSttDefault"), ("openai/gpt-4o-transcribe", "ModelNoteSttAccurate")],
        ["OpenAI"] = [("gpt-4o-mini-transcribe", "ModelNoteSttDefault"), ("gpt-4o-transcribe", "ModelNoteSttAccurate"), ("whisper-1", "ModelNoteSttWhisper")],
        ["Groq"] = [("whisper-large-v3-turbo", "ModelNoteSttFast"), ("whisper-large-v3", "ModelNoteSttAccurate")]
    };

    // Without a "type" field, /models of OpenAI and others mixes in audio, image and embedding models.
    private static readonly Regex NotChat = new(
        "whisper|tts|transcri|embed|dall-e|moderation|image|audio|realtime|search|davinci|babbage|sora|rerank",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Speech = new("whisper|transcri|speech-to-text|asr", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static IReadOnlyList<(string Id, string NoteKey)> Suggestions(string preset, ModelKind kind)
    {
        var table = kind == ModelKind.Chat ? ChatSuggestions : SpeechSuggestions;
        return table.TryGetValue(preset ?? string.Empty, out var items) ? items : [];
    }

    public static bool TryGetSttPreset(string preset, out string baseUrl, out string model)
    {
        if (SttPresetDefaults.TryGetValue(preset ?? string.Empty, out var defaults))
        {
            (baseUrl, model) = defaults;
            return true;
        }

        baseUrl = model = string.Empty;
        return false;
    }

    public static string InferSttPreset(string baseUrl)
    {
        foreach ((string preset, (string url, _)) in SttPresetDefaults)
        {
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? actual) &&
                Uri.TryCreate(url, UriKind.Absolute, out Uri? known) &&
                string.Equals(actual.Host, known.Host, StringComparison.OrdinalIgnoreCase))
            {
                return preset;
            }
        }

        return string.IsNullOrWhiteSpace(baseUrl) ? AppSettings.PolzaPresetName : SttCustomPreset;
    }

    /// <summary>Asks the provider which models it serves. Runs off the UI thread; throws when the provider refuses.</summary>
    public static async Task<List<RemoteModel>> FetchAsync(
        string baseUrl,
        string apiKey,
        string keyHeader,
        string keyPrefix,
        ModelKind kind,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl.Trim().TrimEnd('/') + "/models");
        request.Headers.AcceptLanguage.ParseAdd("ru");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            string header = string.IsNullOrWhiteSpace(keyHeader) ? "Authorization" : keyHeader.Trim();
            string value = string.IsNullOrWhiteSpace(keyPrefix) ? apiKey.Trim() : keyPrefix.Trim() + " " + apiKey.Trim();
            request.Headers.TryAddWithoutValidation(header, value);
        }

        using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("HTTP " + (int)response.StatusCode);
        }

        return await Task.Run(() => Parse(body, kind), token).ConfigureAwait(false);
    }

    private static List<RemoteModel> Parse(string json, ModelKind kind)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        JsonElement items = root.ValueKind == JsonValueKind.Array ? root
            : root.TryGetProperty("data", out JsonElement data) ? data
            : root.TryGetProperty("models", out JsonElement models) ? models
            : default;
        if (items.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("no model list in the answer");
        }

        var result = new List<RemoteModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string id = Text(item, "id") ?? Text(item, "name") ?? string.Empty;
            if (id.Length == 0 || !seen.Add(id) || !Matches(item, id, kind))
            {
                continue;
            }

            string name = Text(item, "name") ?? string.Empty;
            (decimal? priceIn, decimal? priceOut, string currency) = kind == ModelKind.Chat ? Prices(item) : (null, null, string.Empty);
            result.Add(new RemoteModel(id, string.Equals(name, id, StringComparison.OrdinalIgnoreCase) ? string.Empty : name, priceIn, priceOut, currency));
        }

        result.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static bool Matches(JsonElement item, string id, ModelKind kind)
    {
        string type = Text(item, "type") ?? string.Empty;
        bool transcribes = type.Equals("stt", StringComparison.OrdinalIgnoreCase) || Speech.IsMatch(id) ||
            (item.TryGetProperty("endpoints", out JsonElement endpoints) && endpoints.ValueKind == JsonValueKind.Array &&
             endpoints.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.String && e.GetString()!.Contains("transcriptions", StringComparison.OrdinalIgnoreCase)));
        if (kind == ModelKind.Speech)
        {
            return transcribes;
        }

        return type.Length > 0 ? type.Equals("chat", StringComparison.OrdinalIgnoreCase) : !transcribes && !NotChat.IsMatch(id);
    }

    /// <summary>Polza.ai: top_provider.pricing per million in rubles; OpenRouter: pricing per token in dollars.</summary>
    private static (decimal? In, decimal? Out, string Currency) Prices(JsonElement item)
    {
        if (item.TryGetProperty("top_provider", out JsonElement top) && top.ValueKind == JsonValueKind.Object &&
            top.TryGetProperty("pricing", out JsonElement polza) && polza.ValueKind == JsonValueKind.Object)
        {
            return (Number(polza, "prompt_per_million"), Number(polza, "completion_per_million"), Text(polza, "currency") ?? "RUB");
        }

        if (item.TryGetProperty("pricing", out JsonElement pricing) && pricing.ValueKind == JsonValueKind.Object &&
            Number(pricing, "prompt") is decimal perTokenIn && Number(pricing, "completion") is decimal perTokenOut)
        {
            return (perTokenIn * 1_000_000m, perTokenOut * 1_000_000m, "USD");
        }

        return (null, null, string.Empty);
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static decimal? Number(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String &&
            decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsed)
            ? parsed
            : null;
    }
}
