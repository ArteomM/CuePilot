using System.IO;
using System.Text;
using CuePilotDesktop.Models;
using CuePilotDesktop.Services;

namespace CuePilotDesktop.ViewModels;

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
        bool useApi = UseApiStt;
        string apiKey = useApi ? ResolveSttApiKey() : string.Empty;
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
            ? string.Format(L["MediaEngineApi"], SttApiModel.Trim())
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
            SttGlossary = SttGlossary,
            UseApiStt = useApi,
            SttApiUrl = string.IsNullOrWhiteSpace(SttApiBaseUrl) ? AppSettings.PolzaBaseUrl : SttApiBaseUrl.Trim(),
            SttApiModel = SttApiModel.Trim(),
            SttApiKey = apiKey
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
