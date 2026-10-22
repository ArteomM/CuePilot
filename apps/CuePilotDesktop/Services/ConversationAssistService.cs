using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net.Http;
using System.IO;
using CuePilotDesktop.Models;

namespace CuePilotDesktop.Services;

public partial class ConversationAssistService
{
    public const string SummaryActionItemsHeading = "## Задачи";
    public const string SummaryHighlightsHeading = "## Ключевые моменты";
    public const string SummaryParticipantsHeading = "## Участники";

    private static readonly (string English, string Russian)[] LegacySummaryHeadings =
    [
        ("## Action Items", SummaryActionItemsHeading),
        ("## Conversation Highlights", SummaryHighlightsHeading),
        ("## Participant Notes", SummaryParticipantsHeading)
    ];

    private readonly OpenAiAssistClient _openAiClient;
    private static readonly HttpClient WebHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(4)
    };

    public ConversationAssistService(OpenAiAssistClient openAiClient)
    {
        _openAiClient = openAiClient;
    }

    public async Task<AssistResult> BuildWhatShouldISayAsync(
        SessionSummary session,
        string userPrompt,
        AssistOptions options,
        CancellationToken cancellationToken = default)
    {
        bool smartModeEnabled = options.SmartModeEnabled;
        AppSettings settings = options.Settings;

        string transcriptContext = BuildTranscriptContext(session, userPrompt, 26);
        string webContext = await BuildWebContextAsync(userPrompt, cancellationToken);
        string visualContext = BuildVisualContext(session, settings, out List<AssistImageInput> images);
        string systemPrompt = ResolveModeSystemPrompt(
            session,
            smartModeEnabled,
            settings,
            "You are a meeting copilot assistant. Answer the user's question using session context first, then web context if relevant. Think carefully and return only the final answer.");
        string fullPrompt =
            $"Session title: {session.Title}\n" +
            $"Session mode: {session.ModeName}\n" +
            $"User request: {userPrompt}\n" +
            $"Transcript context:\n{transcriptContext}\n\n" +
            $"Visual context:\n{visualContext}\n\n" +
            $"Web context:\n{webContext}\n\n" +
            "Rules: 2-4 short sentences max, answer only in Russian, no markdown, no hallucinations. " +
            "If web context is missing or weak, say this briefly and continue with session context only.";

        LlmResponse response = await CompleteAsync(options, AssistModelTier.Fast, systemPrompt, fullPrompt, images, cancellationToken);
        return AssistResult.From(response, LimitSentenceCount(response.Text, 3));
    }

    /// <summary>Ready-to-say phrases for the user's next turn, based on the latest remarks.</summary>
    public async Task<AssistResult> BuildReplySuggestionAsync(
        SessionSummary session,
        AssistOptions options,
        CancellationToken cancellationToken = default)
    {
        bool smartModeEnabled = options.SmartModeEnabled;
        AppSettings settings = options.Settings;

        string transcriptContext = BuildTranscriptContext(session, null, 20);
        string visualContext = BuildVisualContext(session, settings, out List<AssistImageInput> images);
        string systemPrompt = ResolveModeSystemPrompt(
            session,
            smartModeEnabled,
            settings,
            "You are a live meeting copilot. Suggest what the user should say right now.");
        string fullPrompt =
            $"Session title: {session.Title}\n" +
            $"User is speaker \"{settings.MicParticipantName}\" (MIC), the other side is \"{settings.SystemParticipantName}\" (SYSTEM).\n" +
            $"Transcript context (latest lines last):\n{transcriptContext}\n\n" +
            $"Visual context:\n{visualContext}\n\n" +
            "Task: react to the latest remarks of the other side and give the user a reply to say out loud now. " +
            "Return 1-2 short variants in Russian, each on its own line starting with \"- \", " +
            "each up to 2 sentences, in first person, natural spoken language, no markdown headers, no explanations. " +
            "If the context is too thin, suggest a polite clarifying reply.";

        LlmResponse response = await CompleteAsync(options, AssistModelTier.Fast, systemPrompt, fullPrompt, images, cancellationToken);
        return AssistResult.From(response, response.Text.Trim());
    }

    public async Task<AssistResult> BuildNextQuestionSuggestionAsync(
        SessionSummary session,
        AssistOptions options,
        CancellationToken cancellationToken = default)
    {
        bool smartModeEnabled = options.SmartModeEnabled;
        AppSettings settings = options.Settings;

        string transcriptContext = BuildTranscriptContext(session, null, 20);
        string webContext = await BuildWebContextAsync(session.Title, cancellationToken);
        string visualContext = BuildVisualContext(session, settings, out List<AssistImageInput> images);
        string systemPrompt = ResolveModeSystemPrompt(
            session,
            smartModeEnabled,
            settings,
            "You are a meeting copilot. Generate one high-value next clarifying question based on conversation context.");
        string fullPrompt =
            $"Session title: {session.Title}\n" +
            $"Transcript context:\n{transcriptContext}\n\n" +
            $"Visual context:\n{visualContext}\n\n" +
            $"Web context:\n{webContext}\n\n" +
            "Return exactly one question in Russian, no markdown.";

        LlmResponse response = await CompleteAsync(options, AssistModelTier.Fast, systemPrompt, fullPrompt, images, cancellationToken);
        return AssistResult.From(response, NormalizeSingleQuestion(response.Text));
    }

    public async Task<AssistResult> BuildFollowUpQuestionsAsync(
        SessionSummary session,
        string userPrompt,
        AssistOptions options,
        CancellationToken cancellationToken = default)
    {
        bool smartModeEnabled = options.SmartModeEnabled;
        AppSettings settings = options.Settings;

        string transcriptContext = BuildTranscriptContext(session, userPrompt, 18);
        string visualContext = BuildVisualContext(session, settings, out List<AssistImageInput> images);
        string systemPrompt = ResolveModeSystemPrompt(
            session,
            smartModeEnabled,
            settings,
            "You are a meeting copilot. Generate only clarifying follow-up questions grounded in the latest transcript context.");
        string fullPrompt =
            $"Session title: {session.Title}\n" +
            $"Current request: {userPrompt}\n" +
            $"Recent transcript context:\n{transcriptContext}\n\n" +
            $"Visual context:\n{visualContext}\n\n" +
            "Output exactly 3 to 5 questions. No answers. No markdown headers.";

        LlmResponse response = await CompleteAsync(options, AssistModelTier.Fast, systemPrompt, fullPrompt, images, cancellationToken);
        return AssistResult.From(response, NormalizeQuestionList(response.Text));
    }

    public async Task<AssistResult> BuildRecapAsync(
        SessionSummary session,
        AssistOptions options,
        CancellationToken cancellationToken = default)
    {
        bool smartModeEnabled = options.SmartModeEnabled;
        AppSettings settings = options.Settings;

        string transcriptContext = BuildTranscriptContext(session, null, 24);
        string visualContext = BuildVisualContext(session, settings, out List<AssistImageInput> images);
        string systemPrompt = ResolveModeSystemPrompt(
            session,
            smartModeEnabled,
            settings,
            "You are a meeting copilot. Produce a compact, high-fidelity recap of the latest discussion.");
        string fullPrompt =
            $"Session title: {session.Title}\n" +
            $"Transcript context:\n{transcriptContext}\n\n" +
            $"Visual context:\n{visualContext}\n\n" +
            "Output a single recap up to 70 words, factual and concise, no markdown. " +
            "Write in Russian language.";

        LlmResponse response = await CompleteAsync(options, AssistModelTier.Fast, systemPrompt, fullPrompt, images, cancellationToken);
        return AssistResult.From(response, LimitWords(response.Text, 70));
    }

    public async Task<AssistResult> BuildSessionSummaryAsync(
        SessionSummary session,
        AssistOptions options,
        CancellationToken cancellationToken = default)
    {
        bool smartModeEnabled = options.SmartModeEnabled;
        AppSettings settings = options.Settings;

        string transcriptContext = BuildSummaryTranscriptContext(session);
        string visualContext = BuildVisualContext(session, settings, out List<AssistImageInput> images);
        string systemPrompt = ResolveModeSystemPrompt(
            session,
            smartModeEnabled,
            settings,
            "You are an AI tasked with creating a concise, structured summary of a meeting based on its raw transcript. " +
            "Use Russian language. Be factual, neutral, terse. Do not add filler phrases, opinions, or extra commentary. " +
            "Preserve terminology and proper nouns exactly as in transcript. Output only the formatted summary.");
        string smartHint = smartModeEnabled
            ? "Smart mode is enabled: increase extraction quality for topic shifts, decisions, and explicit tasks."
            : "Normal mode: keep extraction concise and deterministic.";
        string fullPrompt =
            "You are an AI assistant tasked with creating a concise, structured summary of a meeting from its transcript. " +
            "The transcript is provided below in the format \"SPEAKER_NAME: text\". Perform the following:\n\n" +
            "1. Identify all explicit action items. For each item extract:\n" +
            "   - What needs to be done\n" +
            "   - Who is responsible (if mentioned)\n" +
            "   - Any deadline or condition (if mentioned)\n" +
            "   Format each item as: \"- <description> **(Ответственный: <Name>, Срок: <date/condition>)**\". " +
            "If no responsible person or deadline is given, omit that part.\n\n" +
            "2. Determine the main topics of the conversation. For each topic, select the most informative 1-2 sentences that capture the essence of the discussion.\n\n" +
            "3. Detect any turning points or notable shifts in the conversation (e.g., change of subject, clarification, decision).\n\n" +
            "4. Produce the final summary in exactly the following Markdown structure (keep the headings exactly as written):\n\n" +
            SummaryActionItemsHeading + "\n" +
            "- ...\n\n" +
            SummaryHighlightsHeading + "\n" +
            "- <Тема 1>: <ключевые предложения>\n" +
            "- <Тема 2>: <ключевые предложения>\n" +
            "- ...\n\n" +
            SummaryParticipantsHeading + "\n" +
            "- <Name>: <main тезисы этого участника>\n" +
            "- ...\n\n" +
            "Requirements:\n" +
            "- Write in Russian.\n" +
            "- Use a neutral, factual tone; no personal opinions.\n" +
            "- Keep each bullet <= 30 words.\n" +
            "- Preserve speaker names exactly as given in transcript.\n" +
            "- Preserve proper nouns and specific terminology as they appear in the transcript.\n" +
            "- If the transcript contains nonsensical or unintelligible sections, note this briefly in the highlights (e.g., \"Некоторая часть реплик была бессвязной\").\n" +
            "- If no explicit action items are found, write \"*Нет явных действий*\" under the \"" + SummaryActionItemsHeading + "\" section.\n" +
            $"- Mode note: {smartHint}\n\n" +
            $"Visual context:\n{visualContext}\n\n" +
            "Transcript:\n" +
            transcriptContext + "\n\n" +
            "Generate only the formatted summary; do not add any explanations or pre-amble.";

        LlmResponse response = await CompleteAsync(options, AssistModelTier.Analysis, systemPrompt, fullPrompt, images, cancellationToken);
        return AssistResult.From(response, EnsureSummaryStructure(response.Text));
    }

    public async Task<AssistResult> BuildFollowUpEmailAsync(
        SessionSummary session,
        AssistOptions options,
        CancellationToken cancellationToken = default)
    {
        bool smartModeEnabled = options.SmartModeEnabled;
        AppSettings settings = options.Settings;

        string summary = string.IsNullOrWhiteSpace(session.SummaryText)
            ? BuildTranscriptContext(session)
            : session.SummaryText;
        string visualContext = BuildVisualContext(session, settings, out List<AssistImageInput> images);
        string systemPrompt = ResolveModeSystemPrompt(
            session,
            smartModeEnabled,
            settings,
            "You are a meeting copilot. Draft a polished follow-up email with clear structure, action owners, and deadlines.");
        string fullPrompt =
            $"Session title: {session.Title}\n" +
            $"Session summary/context:\n{summary}\n\n" +
            $"Visual context:\n{visualContext}\n\n" +
            "Return a ready-to-send email draft in Russian plain text: subject line, greeting, short recap, " +
            "action items with owners and deadlines, closing. No markdown.";

        LlmResponse response = await CompleteAsync(options, AssistModelTier.Analysis, systemPrompt, fullPrompt, images, cancellationToken);
        return AssistResult.From(response, response.Text);
    }

    private Task<LlmResponse> CompleteAsync(
        AssistOptions options,
        AssistModelTier tier,
        string systemPrompt,
        string userPrompt,
        IReadOnlyList<AssistImageInput> images,
        CancellationToken cancellationToken)
    {
        bool analysis = tier == AssistModelTier.Analysis;
        string model = analysis && !string.IsNullOrWhiteSpace(options.AnalysisModel)
            ? options.AnalysisModel
            : options.FastModel;

        // Live hints must be fast: minimal thinking by default, more in Smart mode.
        string? reasoningEffort = analysis
            ? (options.SmartModeEnabled ? "medium" : null)
            : (options.SmartModeEnabled ? "medium" : "low");

        if (IsEnglish(options.Settings))
        {
            // Prompts are written for Russian output; the English interface switches every answer to English.
            systemPrompt = systemPrompt.Replace("Russian", "English") + " Always answer in English.";
            userPrompt = userPrompt.Replace("Russian", "English");
        }

        return _openAiClient.CompleteAsync(
            options.Endpoint,
            new LlmRequest
            {
                Model = model,
                SystemPrompt = systemPrompt,
                UserPrompt = userPrompt,
                Images = images,
                ReasoningEffort = reasoningEffort
            },
            cancellationToken);
    }

    private static string ResolveModeSystemPrompt(
        SessionSummary session,
        bool smartModeEnabled,
        AppSettings settings,
        string fallbackPrompt)
    {
        string modePrompt = session.ModeRealtimePrompt;
        string modeNotesTemplate = session.ModeNotesTemplate;
        if (string.IsNullOrWhiteSpace(modePrompt))
        {
            modePrompt = string.Equals(session.ModeName, AppSettings.DefaultModeName, StringComparison.OrdinalIgnoreCase)
                ? settings.DefaultRealtimePrompt
                : settings.GeneralRealtimePrompt;
        }

        if (string.IsNullOrWhiteSpace(modeNotesTemplate))
        {
            modeNotesTemplate = string.Equals(session.ModeName, AppSettings.DefaultModeName, StringComparison.OrdinalIgnoreCase)
                ? settings.DefaultNotesTemplate
                : settings.GeneralNotesTemplate;
        }

        string modeGuidance = string.IsNullOrWhiteSpace(modePrompt)
            ? fallbackPrompt
            : modePrompt.Trim();
        if (!string.IsNullOrWhiteSpace(modeNotesTemplate))
        {
            modeGuidance += "\nNotes template preference: " + modeNotesTemplate.Trim();
        }
        if (!smartModeEnabled)
        {
            return modeGuidance;
        }

        return modeGuidance + " Keep output concise and grounded in the latest evidence.";
    }

    private static string BuildVisualContext(SessionSummary session, AppSettings settings, out List<AssistImageInput> images)
    {
        images = [];
        if (session.VisualFrames.Count == 0)
        {
            return "(no visual context)";
        }

        int imageLimit = Math.Clamp(settings.VisualContextMaxImagesPerRequest, 1, 5);
        IEnumerable<SessionVisualFrame> latest = session.VisualFrames
            .OrderByDescending(f => f.Timestamp)
            .Take(Math.Max(imageLimit * 2, 6))
            .ToArray();

        var summary = new StringBuilder();
        int added = 0;
        foreach (SessionVisualFrame frame in latest)
        {
            summary.Append("- ")
                .Append(frame.Timestamp.ToString("HH:mm:ss"))
                .Append(" [")
                .Append(frame.Source)
                .Append("] ")
                .Append(string.IsNullOrWhiteSpace(frame.WindowTitle) ? "window-unknown" : frame.WindowTitle)
                .Append(" | delta=")
                .Append(frame.DifferenceScore)
                .Append(" | ")
                .AppendLine(LimitWords(frame.OcrText, 20));

            if (added < imageLimit && !string.IsNullOrWhiteSpace(frame.FilePath) && File.Exists(frame.FilePath))
            {
                images.Add(new AssistImageInput
                {
                    FilePath = frame.FilePath,
                    Detail = "low"
                });
                added++;
            }
        }

        return summary.Length == 0 ? "(no visual context)" : summary.ToString().TrimEnd();
    }

    private static string BuildTranscriptContext(SessionSummary session, string? query = null, int maxLines = 20)
    {
        if (session.Transcript.Count == 0)
        {
            return "(no transcript yet)";
        }

        IEnumerable<SessionTranscriptItem> source = session.Transcript;
        string[] queryTokens = TokenizeQuery(query);
        if (queryTokens.Length > 0 && session.Transcript.Count > maxLines)
        {
            source = session.Transcript
                .Select(item => new
                {
                    Item = item,
                    Score = queryTokens.Count(t =>
                        (!string.IsNullOrWhiteSpace(item.SummaryReadyText) && item.SummaryReadyText.Contains(t, StringComparison.OrdinalIgnoreCase)) ||
                        item.Text.Contains(t, StringComparison.OrdinalIgnoreCase))
                })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Item.Timestamp)
                .Take(Math.Max(8, maxLines / 2))
                .Select(x => x.Item)
                .Concat(session.Transcript.TakeLast(Math.Max(6, maxLines / 3)))
                .DistinctBy(item => item.Timestamp.ToString("O") + "|" + item.Text)
                .OrderBy(item => item.Timestamp)
                .Take(maxLines);
        }
        else
        {
            source = session.Transcript.TakeLast(maxLines);
        }

        var builder = new StringBuilder();
        foreach (SessionTranscriptItem item in source)
        {
            string speaker = ResolveSpeakerName(item);
            builder.Append(item.Timestamp.ToString("HH:mm:ss"))
                .Append(": ")
                .Append(speaker)
                .Append(": ")
                .AppendLine(item.Text);
        }

        return builder.ToString();
    }

    private static string BuildSummaryTranscriptContext(SessionSummary session)
    {
        if (session.Transcript.Count == 0)
        {
            return "(no transcript yet)";
        }

        var lines = new List<string>(session.Transcript.Count);
        string previous = string.Empty;
        foreach (SessionTranscriptItem item in session.Transcript)
        {
            string text = item.SummaryReadyText;
            if (string.IsNullOrWhiteSpace(text))
            {
                text = TranscriptTextPipeline.BuildSummaryReadyText(ResolveSpeakerName(item), item.Text);
            }

            text = NormalizeTranscriptText(text);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            // Drop obvious duplicated neighboring lines from interim/final ASR passes.
            if (string.Equals(previous, text, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            lines.Add(text);
            previous = text;
        }

        return lines.Count == 0 ? "(no transcript yet)" : string.Join('\n', lines);
    }

    private static string NormalizeTranscriptText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string normalized = text.Normalize(NormalizationForm.FormKC);
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return normalized;
    }

    private static string ResolveSpeakerName(SessionTranscriptItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.SpeakerName))
        {
            return item.SpeakerName.Trim();
        }

        return item.Source == SessionSource.Mic ? "Speaker 1" : "Speaker 2";
    }

    private static string[] TokenizeQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        return Regex.Split(query, @"\W+")
            .Where(token => token.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string LimitSentenceCount(string text, int maxSentences)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string cleaned = Regex.Replace(text, @"\s+", " ").Trim();
        string[] sentences = Regex.Split(cleaned, @"(?<=[.!?])\s+")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Take(maxSentences)
            .ToArray();

        if (sentences.Length == 0)
        {
            return cleaned;
        }

        return string.Join(" ", sentences).Trim();
    }

    private static string LimitWords(string text, int maxWords)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string[] words = Regex.Split(Regex.Replace(text, @"\s+", " ").Trim(), @"\s+")
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .ToArray();
        if (words.Length <= maxWords)
        {
            return string.Join(" ", words);
        }

        return string.Join(" ", words.Take(maxWords));
    }

    private static string NormalizeQuestionList(string raw)
    {
        var lines = raw
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => Regex.Replace(line, @"^\s*[-*\d\.\)]*\s*", string.Empty))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Trim())
            .ToList();

        if (lines.Count == 0)
        {
            lines = Regex.Split(raw, @"(?<=[\?\.\!])\s+")
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => line.Trim())
                .ToList();
        }

        if (lines.Count == 0)
        {
            lines =
            [
                "Что для вас является главным приоритетом?",
                "Какой следующий шаг вы считаете оптимальным?",
                "Какие риски нужно учесть заранее?"
            ];
        }

        var normalized = new List<string>();
        foreach (string line in lines)
        {
            string question = line.EndsWith("?") ? line : line + "?";
            normalized.Add(question);
            if (normalized.Count >= 5)
            {
                break;
            }
        }

        while (normalized.Count < 3)
        {
            normalized.Add("Какой следующий шаг вы считаете оптимальным?");
        }

        return string.Join(Environment.NewLine, normalized.Select(q => "- " + q));
    }

    private static string EnsureSummaryStructure(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string text = LocalizeSummaryHeadings(raw.Trim());
        bool hasActionItems = text.Contains(SummaryActionItemsHeading, StringComparison.OrdinalIgnoreCase);
        bool hasHighlights = text.Contains(SummaryHighlightsHeading, StringComparison.OrdinalIgnoreCase);

        if (hasActionItems && hasHighlights)
        {
            return text;
        }

        var builder = new StringBuilder();
        builder.AppendLine(SummaryActionItemsHeading);
        builder.AppendLine("- *Нет явных действий*");
        builder.AppendLine();
        builder.AppendLine(SummaryHighlightsHeading);
        builder.AppendLine("- " + LimitWords(Regex.Replace(text, @"\s+", " "), 30));
        return builder.ToString().TrimEnd();
    }

    private static bool IsEnglish(AppSettings settings)
    {
        return string.Equals(settings.OutputLanguage, "en", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Summaries are stored with Russian headings (older ones with English): show them in the interface language.
    /// </summary>
    public static string LocalizeSummaryHeadings(string text, bool english = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        foreach ((string englishHeading, string russianHeading) in LegacySummaryHeadings)
        {
            text = english
                ? text.Replace(russianHeading, englishHeading, StringComparison.OrdinalIgnoreCase)
                : text.Replace(englishHeading, russianHeading, StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }

    private static string NormalizeSingleQuestion(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string question = Regex.Replace(raw, @"^\s*[-*\d\.\)]*\s*", string.Empty).Trim();
        question = Regex.Split(question, @"(?<=[\?\.\!])\s+")
            .FirstOrDefault(part => !string.IsNullOrWhiteSpace(part))?.Trim() ?? question;
        if (!question.EndsWith("?", StringComparison.Ordinal))
        {
            question += "?";
        }

        return question;
    }

    private static async Task<string> BuildWebContextAsync(string? query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "(no web context)";
        }

        try
        {
            string encoded = Uri.EscapeDataString(query.Trim());
            string url = $"https://api.duckduckgo.com/?q={encoded}&format=json&no_html=1&skip_disambig=1";
            using HttpResponseMessage response = await WebHttpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return "(no web context)";
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument doc = JsonDocument.Parse(json);
            var snippets = new List<string>();

            if (doc.RootElement.TryGetProperty("AbstractText", out JsonElement abstractText))
            {
                string value = abstractText.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    snippets.Add(value.Trim());
                }
            }

            if (doc.RootElement.TryGetProperty("Answer", out JsonElement answerText))
            {
                string value = answerText.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    snippets.Add(value.Trim());
                }
            }

            if (doc.RootElement.TryGetProperty("RelatedTopics", out JsonElement relatedTopics) &&
                relatedTopics.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in relatedTopics.EnumerateArray())
                {
                    if (snippets.Count >= 5)
                    {
                        break;
                    }

                    if (item.TryGetProperty("Text", out JsonElement textNode))
                    {
                        string text = textNode.GetString() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            snippets.Add(text.Trim());
                        }
                        continue;
                    }

                    if (item.TryGetProperty("Topics", out JsonElement nested) &&
                        nested.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement nestedItem in nested.EnumerateArray())
                        {
                            if (snippets.Count >= 5)
                            {
                                break;
                            }

                            if (!nestedItem.TryGetProperty("Text", out JsonElement nestedTextNode))
                            {
                                continue;
                            }

                            string nestedText = nestedTextNode.GetString() ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(nestedText))
                            {
                                snippets.Add(nestedText.Trim());
                            }
                        }
                    }
                }
            }

            if (snippets.Count == 0)
            {
                return "(no web context)";
            }

            return string.Join(Environment.NewLine, snippets.Select(s => "- " + LimitWords(s, 28)));
        }
        catch
        {
            return "(no web context)";
        }
    }
}

public enum AssistModelTier
{
    Fast,
    Analysis
}

public sealed class AssistOptions
{
    public required LlmEndpoint Endpoint { get; init; }
    public required string FastModel { get; init; }
    public string AnalysisModel { get; init; } = string.Empty;
    public bool SmartModeEnabled { get; init; }
    public required AppSettings Settings { get; init; }
}

public sealed class AssistResult
{
    public string Text { get; init; } = string.Empty;
    public AssistTokenUsage? Usage { get; init; }
    public int AttachedImages { get; init; }

    public static AssistResult From(LlmResponse response, string text)
    {
        return new AssistResult
        {
            Text = text,
            Usage = response.Usage,
            AttachedImages = response.AttachedImages
        };
    }
}
