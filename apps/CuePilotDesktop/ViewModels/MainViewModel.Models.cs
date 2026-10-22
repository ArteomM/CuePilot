using System.Collections.ObjectModel;
using System.Globalization;
using CuePilotDesktop.Services;

namespace CuePilotDesktop.ViewModels;

/// <summary>
/// Settings model fields: the speech recognition provider and the lists of the model dropdowns. A few known ids
/// are offered at once; the provider's own list replaces them once its GET /models has answered.
/// </summary>
public partial class MainViewModel
{
    private readonly Dictionary<string, List<RemoteModel>> _remoteModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _remoteModelErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loadingModelLists = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ModelOption> _chatModelOptions = [];
    private IReadOnlyList<ModelOption> _speechModelOptions = [];
    private string _selectedSttPreset = AppSettings.PolzaPresetName;
    private string _sttApiBaseUrl = AppSettings.PolzaBaseUrl;

    public ObservableCollection<string> SttApiPresets { get; } = new(ModelCatalog.SttPresets);

    public RelayCommand LoadChatModelsCommand { get; private set; } = null!;
    public RelayCommand LoadSpeechModelsCommand { get; private set; } = null!;

    public IReadOnlyList<ModelOption> ChatModelOptions => _chatModelOptions;
    public IReadOnlyList<ModelOption> SpeechModelOptions => _speechModelOptions;
    public string ChatModelsStatus => DescribeModelList(ModelKind.Chat);
    public string SpeechModelsStatus => DescribeModelList(ModelKind.Speech);

    public string SelectedSttPreset
    {
        get => _selectedSttPreset;
        set
        {
            if (_selectedSttPreset == value || string.IsNullOrEmpty(value))
            {
                return;
            }

            _selectedSttPreset = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SttBaseUrlFieldVisibility));

            // «Custom» keeps the address and model already there.
            if (ModelCatalog.TryGetSttPreset(value, out string baseUrl, out string model))
            {
                SttApiBaseUrl = baseUrl;
                SttApiModel = model;
            }

            RebuildModelOptions(ModelKind.Speech);
        }
    }

    public string SttApiBaseUrl
    {
        get => _sttApiBaseUrl;
        set
        {
            if (_sttApiBaseUrl == value)
            {
                return;
            }

            _sttApiBaseUrl = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SttKeySourceText));
            RebuildModelOptions(ModelKind.Speech);
        }
    }

    public bool SttBaseUrlFieldVisibility =>
        string.Equals(SelectedSttPreset, ModelCatalog.SttCustomPreset, StringComparison.OrdinalIgnoreCase);

    private void InitializeModelCatalog()
    {
        _sttApiBaseUrl = string.IsNullOrWhiteSpace(_settings.SttApiBaseUrl) ? AppSettings.PolzaBaseUrl : _settings.SttApiBaseUrl.Trim();
        _selectedSttPreset = ModelCatalog.SttPresets.Contains(_settings.SttApiPresetName, StringComparer.OrdinalIgnoreCase)
            ? ModelCatalog.SttPresets.First(p => string.Equals(p, _settings.SttApiPresetName, StringComparison.OrdinalIgnoreCase))
            : ModelCatalog.InferSttPreset(_sttApiBaseUrl);
        LoadChatModelsCommand = new RelayCommand(force => _ = LoadModelListAsync(ModelKind.Chat, force is true));
        LoadSpeechModelsCommand = new RelayCommand(force => _ = LoadModelListAsync(ModelKind.Speech, force is true));
        L.PropertyChanged += (_, _) =>
        {
            RebuildModelOptions(ModelKind.Chat);
            RebuildModelOptions(ModelKind.Speech);
        };
        RebuildModelOptions(ModelKind.Chat);
        RebuildModelOptions(ModelKind.Speech);
    }

    private string ModelListBaseUrl(ModelKind kind) => (kind == ModelKind.Chat ? OpenAiBaseUrl : SttApiBaseUrl).Trim().TrimEnd('/');

    private string ModelListKey(ModelKind kind) => kind + "|" + ModelListBaseUrl(kind);

    /// <summary>Asks the provider for its models once per address; «Обновить» asks again.</summary>
    private async Task LoadModelListAsync(ModelKind kind, bool force)
    {
        string baseUrl = ModelListBaseUrl(kind);
        string key = ModelListKey(kind);
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _) || _loadingModelLists.Contains(key) ||
            (!force && (_remoteModels.ContainsKey(key) || _remoteModelErrors.ContainsKey(key))))
        {
            return;
        }

        _loadingModelLists.Add(key);
        _remoteModelErrors.Remove(key);
        RebuildModelOptions(kind);
        try
        {
            List<RemoteModel> models = kind == ModelKind.Chat
                ? await ModelCatalog.FetchAsync(baseUrl, ResolveLlmApiKey(), ApiKeyHeaderName, ApiKeyPrefix, kind, CancellationToken.None)
                : await ModelCatalog.FetchAsync(baseUrl, ResolveSttApiKey(), "Authorization", "Bearer", kind, CancellationToken.None);
            _remoteModels[key] = models;
            AppendLog($"[models] {baseUrl}: {models.Count} {kind} model(s).");
        }
        catch (Exception ex)
        {
            _remoteModelErrors[key] = ex is TaskCanceledException ? L["ModelListTimeout"] : ex.Message;
            AppendLog($"[models] {baseUrl}: list failed: {ex.Message}");
        }
        finally
        {
            _loadingModelLists.Remove(key);
        }

        // The provider may have been switched meanwhile: this rebuilds whatever is selected now.
        RebuildModelOptions(kind);
    }

    /// <summary>
    /// Known ids first (dropped once the provider's list says it does not serve them), then everything else the
    /// provider lists, alphabetically.
    /// </summary>
    private void RebuildModelOptions(ModelKind kind)
    {
        string preset = kind == ModelKind.Chat ? SelectedApiPreset : SelectedSttPreset;
        _remoteModels.TryGetValue(ModelListKey(kind), out List<RemoteModel>? remote);
        Dictionary<string, RemoteModel>? served = remote?.ToDictionary(model => model.Id, StringComparer.OrdinalIgnoreCase);

        var options = new List<ModelOption>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string id, string noteKey) in ModelCatalog.Suggestions(preset, kind))
        {
            RemoteModel? known = null;
            if (served != null && !served.TryGetValue(id, out known))
            {
                continue;
            }

            if (added.Add(id))
            {
                options.Add(new ModelOption(id, ModelNote(L[noteKey], known), true));
            }
        }

        foreach (RemoteModel model in remote ?? [])
        {
            if (added.Add(model.Id))
            {
                options.Add(new ModelOption(model.Id, ModelNote(string.Empty, model), false));
            }
        }

        if (kind == ModelKind.Chat)
        {
            _chatModelOptions = options;
            OnPropertyChanged(nameof(ChatModelOptions));
            OnPropertyChanged(nameof(ChatModelsStatus));
        }
        else
        {
            _speechModelOptions = options;
            OnPropertyChanged(nameof(SpeechModelOptions));
            OnPropertyChanged(nameof(SpeechModelsStatus));
        }
    }

    /// <summary>«быстрая, для подсказок · Gemini 3.8 Flash · 15,5 / 62 ₽ за 1 млн токенов»</summary>
    private string ModelNote(string note, RemoteModel? model)
    {
        var parts = new List<string>();
        if (note.Length > 0)
        {
            parts.Add(note);
        }

        if (!string.IsNullOrWhiteSpace(model?.Name))
        {
            parts.Add(model.Name);
        }

        if (model is { PriceIn: decimal priceIn, PriceOut: decimal priceOut })
        {
            string currency = model.Currency.ToUpperInvariant() switch
            {
                "RUB" => "₽",
                "USD" => "$",
                _ => model.Currency
            };
            parts.Add(string.Format(L["ModelPrice"], FormatPrice(priceIn), FormatPrice(priceOut), currency));
        }

        return string.Join(" · ", parts);
    }

    private static string FormatPrice(decimal value) =>
        value.ToString(value >= 10 ? "0.#" : "0.##", CultureInfo.CurrentCulture);

    private string DescribeModelList(ModelKind kind)
    {
        string key = ModelListKey(kind);
        if (_loadingModelLists.Contains(key))
        {
            return L["ModelListLoading"];
        }

        if (_remoteModelErrors.TryGetValue(key, out string? error))
        {
            return string.Format(L["ModelListFailed"], error);
        }

        return _remoteModels.TryGetValue(key, out List<RemoteModel>? models)
            ? string.Format(L["ModelListLoaded"], models.Count)
            : L["ModelListHint"];
    }
}
