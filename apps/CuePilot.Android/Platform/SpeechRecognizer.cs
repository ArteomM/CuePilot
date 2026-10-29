namespace CuePilotDesktop.Services;

/// <summary>
/// Turns one piece of 16 kHz mono speech into text: through the speech API (<see cref="SpeechApiClient"/>) or on
/// the phone itself with whisper.cpp (<see cref="LocalWhisperRecognizer"/>), as chosen in Settings.
/// </summary>
public interface ISpeechRecognizer : IDisposable
{
    /// <summary>Recognizes the samples; <paramref name="previousText"/> keeps words cut at the chunk border.</summary>
    string Transcribe(short[] samples, string previousText, CancellationToken token);

    /// <summary>On noise without speech the model may return its own prompt: such text is no speech.</summary>
    bool IsPromptEcho(string text);

    /// <summary>Total cost reported by the API, RUB (0 for local recognition).</summary>
    double CostRub { get; }

    /// <summary>For the log: «API whisper-1» or «whisper.cpp small (q8_0)».</summary>
    string Name { get; }
}

/// <summary>
/// The prompt of DesktopCapture for every engine: the Russian priming phrase (short chunks otherwise drift into
/// Ukrainian spelling), the glossary, the tail of the previous chunk unless that one slipped into Ukrainian.
/// </summary>
public static class SpeechPrompt
{
    public const string RussianPrimingPrompt = "Разговор на русском языке.";
    private static readonly char[] UkrainianOnlyLetters = { 'і', 'ї', 'є', 'ґ', 'І', 'Ї', 'Є', 'Ґ' };

    /// <summary>Glossary lines, commas and semicolons as one comma-separated list.</summary>
    public static string NormalizeGlossary(string? glossary) => string.Join(", ", (glossary ?? string.Empty)
        .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public static string Build(string model, string language, string glossary, string? previousText)
    {
        if (model.Contains("diarize", StringComparison.OrdinalIgnoreCase) ||
            model.StartsWith("aiesa/", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        bool russian = string.Equals(language, "ru", StringComparison.OrdinalIgnoreCase);
        string text = (previousText ?? string.Empty).Trim();
        if (russian && text.IndexOfAny(UkrainianOnlyLetters) >= 0)
        {
            text = string.Empty;
        }

        if (text.Length > 200)
        {
            text = text[^200..];
        }

        var parts = new List<string>();
        if (russian)
        {
            parts.Add(RussianPrimingPrompt);
        }

        if (glossary.Length > 0)
        {
            parts.Add((russian ? "Термины: " : "Terms: ") + glossary + ".");
        }

        parts.Add(text);
        return string.Join(" ", parts).Trim();
    }

    /// <summary>Three or more words that all come from the priming phrase and the glossary are no speech.</summary>
    public static bool IsEcho(string text, string glossary)
    {
        string[] words = Words(text);
        if (words.Length < 3)
        {
            return false;
        }

        var promptWords = new HashSet<string>(Words(RussianPrimingPrompt + " Термины Terms " + glossary), StringComparer.Ordinal);
        return words.All(promptWords.Contains);
    }

    private static string[] Words(string text) =>
        new string((text ?? string.Empty).ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>whisper.cpp on the phone with a model downloaded in Settings; loaded on the first piece, off the UI thread.</summary>
public sealed class LocalWhisperRecognizer : ISpeechRecognizer
{
    private readonly string _modelPath;
    private readonly string _language;
    private readonly string _glossary;
    private LocalWhisper? _whisper;
    private string? _loadError;

    public LocalWhisperRecognizer(string modelPath, string language, string glossary)
    {
        _modelPath = modelPath ?? string.Empty;
        _language = string.IsNullOrWhiteSpace(language) ? "auto" : language;
        _glossary = SpeechPrompt.NormalizeGlossary(glossary);
    }

    public double CostRub => 0;

    public string Name => "whisper.cpp " + (SpeechModelCatalog.Find(Path.GetFileName(_modelPath))?.Title ?? Path.GetFileNameWithoutExtension(_modelPath));

    public string Transcribe(short[] samples, string previousText, CancellationToken token)
    {
        // A model that did not load is not retried for every piece: loading takes seconds and lots of memory.
        if (_whisper == null)
        {
            if (_loadError != null)
            {
                throw new InvalidOperationException(_loadError);
            }

            try
            {
                _whisper = LocalWhisper.Load(_modelPath);
            }
            catch (Exception ex)
            {
                _loadError = ex.Message;
                throw;
            }
        }

        var audio = new float[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            audio[i] = samples[i] / 32768f;
        }

        string prompt = SpeechPrompt.Build("whisper.cpp", _language, _glossary, previousText);
        return _whisper.Transcribe(audio, _language, prompt, token);
    }

    public bool IsPromptEcho(string text) => SpeechPrompt.IsEcho(text, _glossary);

    public void Dispose() => _whisper?.Dispose();
}
