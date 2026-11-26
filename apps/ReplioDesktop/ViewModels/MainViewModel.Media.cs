using System.IO;
using System.Text;
using ReplioDesktop.Models;
using ReplioDesktop.Services;

namespace ReplioDesktop.ViewModels;

/// <summary>
/// Audio and video in «База знаний»: «Разобрать» transcribes the sound with the recognition settings
/// (local whisper or the STT API) and stores the text with time marks and a link to the file.
/// </summary>
public partial class MainViewModel
{
    private readonly Dictionary<string, CancellationTokenSource> _mediaTranscriptions = new();

    public RelayCommand AddKnowledgeMediaCommand { get; private set; } = null!;
    public RelayCommand StopKnowledgeItemCommand { get; private set; } = null!;

    private void InitializeKnowledgeMedia()
    {
        AddKnowledgeMediaCommand = new RelayCommand(AddKnowledgeMedia, () => SelectedTopic != null);
        StopKnowledgeItemCommand = new RelayCommand(
            parameter => StopKnowledgeItem(parameter as KnowledgeItemViewModel ?? SelectedKnowledgeItem),
            parameter => (parameter as KnowledgeItemViewModel ?? SelectedKnowledgeItem) is { CanStop: true });
    }

    private async void AddKnowledgeMedia()
    {
        SessionTopic? topic = SelectedTopic;
        if (topic == null)
        {
            return;
        }

        IReadOnlyList<string> files = await PickFilesAsync(
            L["KnowledgeAddMediaTitle"], L["KnowledgeMediaFilterName"], MediaTranscriptionService.FileDialogPatterns, true);
        if (files.Count == 0)
        {
            return;
        }

        AddKnowledgeSources(topic, files.Select(MediaSource));
    }

    /// <summary>
    /// Adds recordings made outside Replio (the phone's own call recorder, a file shared to Replio) to the chosen
    /// topic, or to a topic named <paramref name="fallbackTopicName"/> when none is chosen, and starts transcribing them
    /// like «+ Медиа». Used by the Android app; returns the topic.
    /// </summary>
    public SessionTopic ImportMediaFiles(IReadOnlyList<string> paths, string fallbackTopicName)
    {
        SessionTopic? topic = SelectedTopic ?? _topics.FirstOrDefault(t => string.Equals(t.Name, fallbackTopicName, StringComparison.CurrentCultureIgnoreCase));
        if (topic == null)
        {
            topic = new SessionTopic { Name = fallbackTopicName };
            _topics.Add(topic);
            SaveTopics();
            RebuildTopicItems();
        }

        if (!ReferenceEquals(SelectedTopic, topic))
        {
            SelectedTopicItem = TopicItems.FirstOrDefault(item => item.Topic == topic);
        }

        AddKnowledgeSources(topic, paths.Select(MediaSource));
        var added = new HashSet<string>(paths, StringComparer.Ordinal);
        foreach (KnowledgeItemViewModel item in KnowledgeItems.Where(i => added.Contains(i.Source.Path)).ToList())
        {
            if (ParseKnowledgeItemCommand.CanExecute(item))
            {
                ParseKnowledgeItemCommand.Execute(item);
            }
        }

        return topic;
    }

    private static KnowledgeSource MediaSource(string path) => new()
    {
        Kind = KnowledgeSourceKind.Media,
        Title = Path.GetFileName(path),
        Path = path
    };

    private void StopKnowledgeItem(KnowledgeItemViewModel? item)
    {
        if (item != null && _mediaTranscriptions.TryGetValue(item.Id, out CancellationTokenSource? cancellation))
        {
            cancellation.Cancel();
            item.ProgressText = L["KnowledgeStopping"];
            OnKnowledgeHeaderChanged();
        }
    }

    /// <summary>
    /// Transcribes a media source and returns its Markdown. Throws with a readable reason when there is no speech
    /// engine or the sound cannot be read, and <see cref="OperationCanceledException"/> when stopped.
    /// </summary>
    private async Task<string> TranscribeMediaSourceAsync(KnowledgeItemViewModel item, KnowledgeSource source)
    {
        (string captureDir, string whisperBin, string whisperModel, string sttDir) = await Task.Run(ResolveCaptureTools);
        if (!CanUseLocalStt)
        {
            whisperBin = whisperModel = string.Empty;
        }
        bool useApi = EffectiveUseApiStt;
        (string apiUrl, string apiModel, string apiKey, string apiHeaders) = useApi ? ResolveSttApi(null) : (string.Empty, string.Empty, string.Empty, string.Empty);
        if (useApi && IsManagedAi && !IsSignedIn)
        {
            throw new InvalidOperationException(L["AccountSignInRequired"]);
        }

        if (useApi && string.IsNullOrWhiteSpace(apiKey))
        {
            useApi = false;
            AppendLog("[media] API transcription is on, but the key is missing: using local whisper.");
        }

        bool hasLocal = whisperBin.Length > 0 && whisperModel.Length > 0;
        if (!useApi && !hasLocal)
        {
            throw new InvalidOperationException(L["MediaNoSpeechEngine"]);
        }

        string engine = useApi
            ? string.Format(L["MediaEngineApi"], IsManagedAi ? "Replio" : apiModel)
            : string.Format(L["MediaEngineLocal"], Path.GetFileNameWithoutExtension(whisperModel));
        var request = new MediaTranscriptionRequest
        {
            MediaPath = source.Path,
            WorkingDirectory = captureDir,
            WhisperBin = whisperBin,
            WhisperModel = whisperModel,
            SttDir = sttDir,
            Language = _settings.CaptureLanguage,
            SilenceThreshold = _settings.SttSilenceThreshold,
            SttGlossary = SttGlossaryFor(_topics.FirstOrDefault(t => t.Sources.Contains(source))),
            UseApiStt = useApi,
            SttApiUrl = apiUrl,
            SttApiModel = apiModel,
            SttApiKey = apiKey,
            SttExtraHeaders = apiHeaders
        };

        // Progress arrives on the UI thread (Progress<T> captures it), so the row and the header can be updated directly.
        var progress = new Progress<MediaTranscriptionProgress>(p =>
        {
            if (_mediaTranscriptions.TryGetValue(item.Id, out CancellationTokenSource? running) && !running.IsCancellationRequested)
            {
                item.ProgressText = p.Total > TimeSpan.Zero
                    ? string.Format(L["KnowledgeTranscribing"], MediaTranscriptionService.FormatTime(p.Done), MediaTranscriptionService.FormatTime(p.Total))
                    : string.Format(L["KnowledgeTranscribingUnknown"], MediaTranscriptionService.FormatTime(p.Done));
                OnKnowledgeHeaderChanged();
            }
        });

        using var cancellation = new CancellationTokenSource();
        _mediaTranscriptions[item.Id] = cancellation;
        item.CanStop = true;
        item.ProgressText = L["KnowledgeExtractingAudio"];
        OnKnowledgeItemsChanged();
        try
        {
            MediaTranscriptionResult result = await Task.Run(() =>
                MediaTranscriptionService.TranscribeAsync(request, progress, AppendLogFromWorker, cancellation.Token));
            AppendLog($"[media] {source.Title}: {result.Segments.Count} piece(s), {result.FailedChunks} failed, {result.CostRub:0.##} RUB.");
            return BuildMediaMarkdown(source, result, engine);
        }
        finally
        {
            _mediaTranscriptions.Remove(item.Id);
            item.CanStop = false;
            StopKnowledgeItemCommand.RaiseCanExecuteChanged();
        }
    }

    private void AppendLogFromWorker(string line) => RunOnUi(() => AppendLog(line));

    /// <summary>Title, a link to the file, length and engine, then the text with a time mark per piece.</summary>
    private string BuildMediaMarkdown(KnowledgeSource source, MediaTranscriptionResult result, string engine)
    {
        var markdown = new StringBuilder();
        markdown.Append("# ").AppendLine(source.Title).AppendLine()
            .Append('_').Append(L["KnowledgeSourceLabel"]).Append(": [").Append(Path.GetFileName(source.Path)).Append("](")
            .Append(MediaTranscriptionService.FileLink(source.Path)).Append(") · ").Append(source.Path).AppendLine("_").AppendLine()
            .Append('_').Append(string.Format(L["MediaTranscriptMeta"], MediaTranscriptionService.FormatTime(result.Duration), engine, DateTime.Now.ToString("g")))
            .AppendLine("_").AppendLine()
            .Append("## ").AppendLine(L["MediaTranscriptHeading"]).AppendLine();
        if (result.Segments.Count == 0)
        {
            markdown.AppendLine(L["MediaNoSpeechFound"]).AppendLine();
        }

        foreach (MediaSegment segment in result.Segments)
        {
            markdown.Append("**[").Append(MediaTranscriptionService.FormatTime(segment.Start)).Append("]** ").AppendLine(segment.Text).AppendLine();
        }

        if (result.FailedChunks > 0)
        {
            markdown.Append('_').Append(string.Format(L["MediaFailedChunks"], result.FailedChunks, result.LastError)).AppendLine("_");
        }

        return markdown.ToString();
    }
}
