using System.Text;
using System.Text.RegularExpressions;

namespace CuePilotDesktop.Services;

public static class TranscriptTextPipeline
{
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex ServicePrefixRegex = new(@"^(system|mic)\s*:\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Whole-line annotations whisper emits for non-speech audio: [музыка], (смех), <empty>, ...
    private static readonly Regex NoiseBracketRegex = new(
        @"^(?:\[[^\]]*\]|\([^\)]*\)|\*[^\*]*\*|<[^>]*>)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Typical whisper hallucinations on silence learned from subtitle credits.
    private static readonly Regex SubtitleCreditRegex = new(
        @"(редактор\s+субтитров|корректор\s+[А-ЯЁA-Z]\.|subtitles?\s+by|субтитры\s+(сделал|создавал|подготовил)|продолжение\s+следует|dimatorzok)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string NormalizeUnicode(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string normalized = text.Normalize(NormalizationForm.FormKC);
        normalized = normalized
            .Replace('“', '"')
            .Replace('”', '"')
            .Replace('‘', '\'')
            .Replace('’', '\'')
            .Replace('–', '-')
            .Replace('—', '-');
        normalized = WhitespaceRegex.Replace(normalized, " ").Trim();
        return normalized;
    }

    public static string BuildFormattedLine(TranscriptSource source, string rawText)
    {
        string speaker = source == TranscriptSource.System ? "SYSTEM" : "MIC";
        string clean = CleanRecognizedText(rawText);
        return string.IsNullOrWhiteSpace(clean) ? string.Empty : $"{speaker}: {clean}";
    }

    public static string BuildSummaryReadyText(string formattedLine)
    {
        if (string.IsNullOrWhiteSpace(formattedLine))
        {
            return string.Empty;
        }

        string normalized = NormalizeUnicode(formattedLine);
        if (ServicePrefixRegex.IsMatch(normalized))
        {
            normalized = ServicePrefixRegex.Replace(normalized, string.Empty).Trim();
        }

        return normalized;
    }

    public static string BuildSummaryReadyText(string speakerName, string rawText)
    {
        string text = CleanRecognizedText(rawText);
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string speaker = NormalizeUnicode(speakerName);
        if (string.IsNullOrWhiteSpace(speaker))
        {
            speaker = "Speaker";
        }

        return $"{speaker}: {text}";
    }

    public static string CleanRecognizedText(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return string.Empty;
        }

        string normalized = NormalizeUnicode(rawText);
        if (ServicePrefixRegex.IsMatch(normalized))
        {
            normalized = ServicePrefixRegex.Replace(normalized, string.Empty).Trim();
        }

        if (SubtitleCreditRegex.IsMatch(normalized))
        {
            return string.Empty;
        }

        if (NoiseBracketRegex.IsMatch(normalized))
        {
            return string.Empty;
        }

        return normalized;
    }
}
