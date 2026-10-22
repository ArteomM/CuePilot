using CuePilotDesktop.Services;

namespace CuePilotDesktop.ViewModels;

/// <summary>One row of the speech model list in Settings: what the model is, whether it suits this PC, its state.</summary>
public class SpeechModelItemViewModel : ObservableObject
{
    private readonly LocalizationService _l;
    private bool _isInstalled;
    private bool _isActive;
    private bool _isDownloading;
    private double _progressPercent;
    private string _progressText = string.Empty;

    public SpeechModelItemViewModel(SpeechModelInfo info, LocalizationService localization)
    {
        Info = info;
        _l = localization;
        // Language switch: every text below is computed from L, so refresh all bindings of the row.
        _l.PropertyChanged += (_, _) => OnPropertyChanged(string.Empty);
    }

    public SpeechModelInfo Info { get; }
    public string Title => Info.Title;
    public string SizeText => string.Format(_l["SpeechModelSize"], Info.ApproxSizeMb);
    public string Description => _l[Info.DescriptionKey];
    public bool RecommendedVisibility => Info.Recommended;
    public bool SuitsThisPc => SpeechModelCatalog.SuitsThisPc(Info);

    public string SuitabilityText => SuitsThisPc
        ? _l["SpeechModelSuitsPc"]
        : string.Format(_l["SpeechModelMaySlow"], Info.MinCores, Info.MinRamGb);

    public string StatusText => IsDownloading
        ? ProgressText
        : IsActive
            ? _l["SpeechModelActive"]
            : IsInstalled ? _l["SpeechModelInstalled"] : _l["SpeechModelNotInstalled"];

    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (_isInstalled == value) return;
            _isInstalled = value;
            OnStateChanged();
        }
    }

    /// <summary>Only models in %LocalAppData%\CuePilot\models can be deleted; one next to the app is left alone.</summary>
    public bool IsDeletable { get; set; }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            OnStateChanged();
        }
    }

    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            if (_isDownloading == value) return;
            _isDownloading = value;
            OnStateChanged();
        }
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        set
        {
            if (Math.Abs(_progressPercent - value) < 0.01) return;
            _progressPercent = value;
            OnPropertyChanged();
        }
    }

    public string ProgressText
    {
        get => _progressText;
        set
        {
            if (_progressText == value) return;
            _progressText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public bool DownloadVisibility => !IsInstalled && !IsDownloading;
    public bool UseVisibility => IsInstalled && !IsActive && !IsDownloading;
    public bool DeleteVisibility => IsInstalled && IsDeletable && !IsDownloading;
    public bool DownloadingVisibility => IsDownloading;

    private void OnStateChanged()
    {
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(DownloadVisibility));
        OnPropertyChanged(nameof(UseVisibility));
        OnPropertyChanged(nameof(DeleteVisibility));
        OnPropertyChanged(nameof(DownloadingVisibility));
    }
}
