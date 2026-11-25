using System.Diagnostics;
using System.Globalization;
using ReplioDesktop.Services;

namespace ReplioDesktop.ViewModels;

/// <summary>
/// Replio account. A user signs in once on replio.ru (device flow); the app then holds a hidden device key and sends
/// every AI and speech request to the Replio gateway, which picks the provider. What the plan allows comes from
/// GET /account:
/// • simple plans: only the prepared models, no provider, key or local-model settings;
/// • «model_choice»: a choice between the prepared models;
/// • «local_stt»: local whisper recognition (special plans);
/// • «custom_providers»: own providers and keys, the full settings of the earlier CuePilot.
/// DeveloperMode in settings.json (not in the UI) opens everything without an account, for development and for
/// users who came from CuePilot with their own keys.
/// </summary>
public partial class MainViewModel
{
    private ReplioClient _replioClient = null!;
    private ReplioAccountInfo? _account;
    private string _replioKey = string.Empty;
    private bool _isSigningIn;
    private bool _isAccountRefreshing;
    private string _signInCode = string.Empty;
    private string _signInUrl = string.Empty;
    private string _accountError = string.Empty;
    private CancellationTokenSource? _signInCancellation;

    public RelayCommand SignInCommand { get; private set; } = null!;
    public RelayCommand CancelSignInCommand { get; private set; } = null!;
    public RelayCommand OpenSignInPageCommand { get; private set; } = null!;
    public RelayCommand SignOutCommand { get; private set; } = null!;
    public RelayCommand RefreshAccountCommand { get; private set; } = null!;
    public RelayCommand OpenUpgradeCommand { get; private set; } = null!;

    public bool IsDeveloperMode => _settings.DeveloperMode;

    public bool IsSignedIn => _replioKey.Length > 0;

    public bool IsSignedOut => !IsSignedIn && !IsSigningIn;

    public bool IsSigningIn
    {
        get => _isSigningIn;
        private set
        {
            if (_isSigningIn == value)
            {
                return;
            }

            _isSigningIn = value;
            OnAccountChanged();
        }
    }

    public string SignInCode => _signInCode;

    /// <summary>Own providers and keys: «Приватный» plan or developer mode.</summary>
    public bool CanUseOwnProviders => IsDeveloperMode || _account?.CustomProviders == true;

    /// <summary>Local whisper: «Локальный» and «Приватный» plans or developer mode.</summary>
    public bool CanUseLocalStt => CanUseOwnProviders || _account?.LocalStt == true;

    /// <summary>AI and speech go through the Replio gateway with the device key.</summary>
    public bool IsManagedAi => !CanUseOwnProviders;

    /// <summary>The account block shows unless developer mode hides it for a setup without an account.</summary>
    public bool ShowAccountSection => !IsDeveloperMode || IsSignedIn;

    public bool ShowReplioModelChoice => IsManagedAi && _account is { ModelChoice: true };

    public bool ShowReplioModelSummary => IsManagedAi && IsSignedIn && !ShowReplioModelChoice;

    public bool ShowOwnProviderSettings => CanUseOwnProviders;

    public bool ShowLocalSttSettings => CanUseLocalStt;

    /// <summary>Technical knobs (snapshot timing and counts): plans with own providers or developer mode only.</summary>
    public bool ShowAdvancedSettings => CanUseOwnProviders;

    public IReadOnlyList<ReplioModelOption> ReplioFastModels => _account?.FastModels ?? [];
    public IReadOnlyList<ReplioModelOption> ReplioAnalysisModels => _account?.AnalysisModels ?? [];
    public IReadOnlyList<ReplioModelOption> ReplioSttModels => _account?.SttModels ?? [];

    public ReplioModelOption? SelectedReplioFastModel
    {
        get => ReplioFastModels.FirstOrDefault(m => m.Id == EffectiveReplioModel(_settings.ReplioFastModel, ReplioFastModels, _account?.DefaultFast));
        set => SetReplioModel(value, model => _settings.ReplioFastModel = model);
    }

    public ReplioModelOption? SelectedReplioAnalysisModel
    {
        get => ReplioAnalysisModels.FirstOrDefault(m => m.Id == EffectiveReplioModel(_settings.ReplioAnalysisModel, ReplioAnalysisModels, _account?.DefaultAnalysis));
        set => SetReplioModel(value, model => _settings.ReplioAnalysisModel = model);
    }

    public ReplioModelOption? SelectedReplioSttModel
    {
        get => ReplioSttModels.FirstOrDefault(m => m.Id == EffectiveReplioModel(_settings.ReplioSttModel, ReplioSttModels, _account?.DefaultStt));
        set => SetReplioModel(value, model => _settings.ReplioSttModel = model);
    }

    /// <summary>«Тариф «Старт» · осталось 300 из 900 мин до 24.12.2026 · устройств 1 из 2»</summary>
    public string AccountPlanText
    {
        get
        {
            if (!IsSignedIn)
            {
                return string.Empty;
            }

            if (_account == null)
            {
                return _isAccountRefreshing ? L["AccountLoading"] : L["AccountUnknown"];
            }

            var parts = new List<string> { string.Format(L["AccountPlan"], _account.PlanName) };
            if (_account.MinutesLeft is int left)
            {
                parts.Add(_account.MinutesTotal is int total
                    ? string.Format(L["AccountMinutesOf"], left, total)
                    : string.Format(L["AccountMinutes"], left));
            }
            else if (_account.AiLeftPercent is int percent)
            {
                parts.Add(string.Format(L["AccountAiLeft"], percent));
            }

            if (_account.PeriodEnd is DateTime end)
            {
                parts.Add(string.Format(L["AccountUntil"], end.ToString("d", CultureInfo.CurrentCulture)));
            }

            if (_account.DevicesMax is int max)
            {
                parts.Add(string.Format(L["AccountDevices"], _account.DevicesUsed ?? 1, max));
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>«Подсказки: Быстрая · Итоги и вопросы: Умная · Распознавание: Стандарт»</summary>
    public string ReplioModelSummaryText => string.Format(
        L["AccountModelsSummary"],
        SelectedReplioFastModel?.Title ?? L["AccountModelDefault"],
        SelectedReplioAnalysisModel?.Title ?? L["AccountModelDefault"],
        CanUseLocalStt && !UseApiStt ? L["AccountSttLocal"] : SelectedReplioSttModel?.Title ?? L["AccountModelDefault"]);

    public string AccountStatusText
    {
        get
        {
            if (IsSigningIn)
            {
                return string.Format(L["AccountSignInWaiting"], _signInCode);
            }

            if (_accountError.Length > 0)
            {
                return _accountError;
            }

            if (!IsSignedIn)
            {
                return IsDeveloperMode ? L["AccountDeveloperMode"] : L["AccountSignedOutHint"];
            }

            return string.Empty;
        }
    }

    private void InitializeAccount()
    {
        if (string.IsNullOrWhiteSpace(_settings.ReplioDeviceId))
        {
            _settings.ReplioDeviceId = Guid.NewGuid().ToString("D");
            PersistSettingsQuietly();
        }

        _replioClient = new ReplioClient(_settings.ReplioDeviceId);
        _replioKey = ReplioKeyStore.Load();
        if (IsSignedIn && !string.IsNullOrWhiteSpace(_settings.ReplioAccountJson))
        {
            try
            {
                _account = ReplioAccountInfo.Parse(_settings.ReplioAccountJson);
            }
            catch (Exception)
            {
                _account = null;
            }
        }

        SignInCommand = new RelayCommand(async () => await SignInAsync(), () => !IsSigningIn);
        CancelSignInCommand = new RelayCommand(() => _signInCancellation?.Cancel(), () => IsSigningIn);
        OpenSignInPageCommand = new RelayCommand(() => OpenUrl(_signInUrl), () => IsSigningIn && _signInUrl.Length > 0);
        SignOutCommand = new RelayCommand(async () => await SignOutAsync(), () => IsSignedIn);
        RefreshAccountCommand = new RelayCommand(async () => await RefreshAccountAsync(), () => IsSignedIn && !_isAccountRefreshing);
        OpenUpgradeCommand = new RelayCommand(() => OpenUrl(_account?.UpgradeUrl ?? "https://replio.ru/account/plan"));

        if (IsSignedIn)
        {
            _ = RefreshAccountAsync();
        }

        StartAccountAutoRefresh();
    }

    private static readonly TimeSpan AccountRefreshInterval = TimeSpan.FromMinutes(10);
    private DateTime _accountRefreshedAt = DateTime.MinValue;
    private UiTimer? _accountTimer;

    /// <summary>
    /// A change of plan in the personal account reaches the app within 10 minutes, at once when Settings open or the
    /// window comes back (if the last check is older than 30 s / 2 min). Limits themselves are checked by the gateway on
    /// every request, so they apply immediately anyway; this keeps the visible options and the balance current.
    /// </summary>
    private void StartAccountAutoRefresh()
    {
        _accountTimer = new UiTimer { Interval = AccountRefreshInterval };
        _accountTimer.Tick += async (_, _) => await RefreshAccountAsync();
        _accountTimer.Start();
    }

    /// <summary>Settings opened or the window activated: refresh unless it was just done.</summary>
    public void RefreshAccountIfStale(int olderThanSeconds = 30)
    {
        if (IsSignedIn && DateTime.UtcNow - _accountRefreshedAt > TimeSpan.FromSeconds(olderThanSeconds))
        {
            _ = RefreshAccountAsync();
        }
    }

    /// <summary>Re-reads the plan: at start, every 10 minutes, on opening Settings, after a recording and after a gateway refusal (402/403).</summary>
    private async Task RefreshAccountAsync()
    {
        if (!IsSignedIn || _isAccountRefreshing)
        {
            return;
        }

        _accountRefreshedAt = DateTime.UtcNow;
        _isAccountRefreshing = true;
        OnAccountChanged();
        try
        {
            string key = _replioKey;
            ReplioAccountInfo account = await Task.Run(() => _replioClient.GetAccountAsync(key, CancellationToken.None));
            ApplyAccount(account);
            _accountError = string.Empty;
            AppendLog($"[account] Plan {account.PlanId}: local_stt={account.LocalStt}, custom_providers={account.CustomProviders}, model_choice={account.ModelChoice}, minutes_left={account.MinutesLeft?.ToString() ?? "-"}.");
        }
        catch (ReplioApiException ex) when (ex.IsSignedOut)
        {
            AppendLog("[account] Key refused: " + ex.Code);
            ForgetKey();
            _accountError = ex.Message;
        }
        catch (Exception ex)
        {
            // Offline or the gateway is down: keep the cached plan, say so quietly.
            _accountError = _account == null ? string.Format(L["AccountRefreshFailed"], ex.Message) : string.Empty;
            AppendLog("[account] Plan not refreshed: " + ex.Message);
        }
        finally
        {
            _isAccountRefreshing = false;
            OnAccountChanged();
        }
    }

    private async Task SignInAsync()
    {
        if (IsSigningIn)
        {
            return;
        }

        _accountError = string.Empty;
        _signInCancellation = new CancellationTokenSource();
        CancellationToken token = _signInCancellation.Token;
        IsSigningIn = true;
        try
        {
            ReplioDeviceLogin login = await Task.Run(() => _replioClient.StartLoginAsync(Environment.MachineName, token), token);
            _signInCode = login.UserCode;
            _signInUrl = login.VerificationUrl;
            OnAccountChanged();
            OpenUrl(login.VerificationUrl);
            AppendLog("[account] Sign-in started, waiting for confirmation on the site.");

            DateTime deadline = DateTime.UtcNow + login.ExpiresIn;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(login.Interval, token);
                ReplioLoginPoll poll = await Task.Run(() => _replioClient.PollLoginAsync(login.DeviceCode, token), token);
                if (poll.State == ReplioLoginState.Pending)
                {
                    continue;
                }

                if (poll.State == ReplioLoginState.Done)
                {
                    string key = poll.Key;
                    await Task.Run(() => ReplioKeyStore.Save(key), token);
                    _replioKey = key;
                    if (poll.Account != null)
                    {
                        ApplyAccount(poll.Account);
                    }

                    AppendLog("[account] Signed in.");
                    ShowFeedback(L["AccountSignedIn"]);
                    if (poll.Account == null)
                    {
                        _ = RefreshAccountAsync();
                    }

                    return;
                }

                _accountError = poll.Message.Length > 0
                    ? poll.Message
                    : L[poll.State == ReplioLoginState.Expired ? "AccountSignInExpired" : "AccountSignInDenied"];
                return;
            }

            _accountError = L["AccountSignInExpired"];
        }
        catch (OperationCanceledException)
        {
            AppendLog("[account] Sign-in cancelled.");
        }
        catch (Exception ex)
        {
            _accountError = ex is ReplioApiException ? ex.Message : string.Format(L["AccountSignInFailed"], ex.Message);
            AppendLog("[account] Sign-in failed: " + ex.Message);
        }
        finally
        {
            _signInCode = string.Empty;
            _signInUrl = string.Empty;
            _signInCancellation?.Dispose();
            _signInCancellation = null;
            IsSigningIn = false;
        }
    }

    private async Task SignOutAsync()
    {
        if (!IsSignedIn)
        {
            return;
        }

        bool confirmed = ConfirmationRequested?.Invoke(L["AccountSignOutTitle"], L["AccountSignOutConfirm"]) ?? true;
        if (!confirmed)
        {
            return;
        }

        string key = _replioKey;
        ForgetKey();
        OnAccountChanged();
        try
        {
            // Revokes the key of this device on the server; the local copy is already gone.
            await Task.Run(() => _replioClient.LogoutAsync(key, CancellationToken.None));
        }
        catch (Exception ex)
        {
            AppendLog("[account] Logout request failed: " + ex.Message);
        }

        AppendLog("[account] Signed out.");
    }

    private void ForgetKey()
    {
        ReplioKeyStore.Delete();
        _replioKey = string.Empty;
        _account = null;
        _settings.ReplioAccountJson = string.Empty;
        PersistSettingsQuietly();
    }

    private void ApplyAccount(ReplioAccountInfo account)
    {
        _account = account;
        _settings.ReplioAccountJson = account.RawJson;
        PersistSettingsQuietly();
        OnAccountChanged();
    }

    /// <summary>A gateway refusal during work: the plan may have changed or the key was revoked.</summary>
    private void OnReplioRefusal(Exception ex)
    {
        if (ex is ReplioApiException { AffectsAccount: true } && IsManagedAi)
        {
            RunOnUi(() => _ = RefreshAccountAsync());
        }
    }

    private void OnAccountChanged()
    {
        foreach (string name in new[]
        {
            nameof(IsSignedIn), nameof(IsSignedOut), nameof(IsSigningIn), nameof(SignInCode), nameof(CanUseOwnProviders),
            nameof(CanUseLocalStt), nameof(IsManagedAi), nameof(ShowAccountSection), nameof(ShowReplioModelChoice),
            nameof(ShowReplioModelSummary), nameof(ShowOwnProviderSettings), nameof(ShowLocalSttSettings), nameof(ShowAdvancedSettings),
            nameof(ReplioFastModels), nameof(ReplioAnalysisModels), nameof(ReplioSttModels), nameof(SelectedReplioFastModel),
            nameof(SelectedReplioAnalysisModel), nameof(SelectedReplioSttModel), nameof(AccountPlanText),
            nameof(ReplioModelSummaryText), nameof(AccountStatusText)
        })
        {
            OnPropertyChanged(name);
        }

        SignInCommand?.RaiseCanExecuteChanged();
        CancelSignInCommand?.RaiseCanExecuteChanged();
        OpenSignInPageCommand?.RaiseCanExecuteChanged();
        SignOutCommand?.RaiseCanExecuteChanged();
        RefreshAccountCommand?.RaiseCanExecuteChanged();
    }

    private void SetReplioModel(ReplioModelOption? value, Action<string> store)
    {
        if (value == null)
        {
            return;
        }

        store(value.Id);
        PersistSettingsQuietly();
        OnAccountChanged();
    }

    /// <summary>The saved choice while the plan still offers it, otherwise the plan's default.</summary>
    private static string EffectiveReplioModel(string? saved, IReadOnlyList<ReplioModelOption> offered, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(saved) && (offered.Count == 0 || offered.Any(m => m.Id == saved)))
        {
            return saved;
        }

        return fallback ?? offered.FirstOrDefault()?.Id ?? string.Empty;
    }

    private string EffectiveFastModel => IsManagedAi
        ? EffectiveReplioModel(_settings.ReplioFastModel, ReplioFastModels, _account?.DefaultFast ?? "replio-fast")
        : OpenAiModel.Trim();

    private string EffectiveAnalysisModel => IsManagedAi
        ? EffectiveReplioModel(_settings.ReplioAnalysisModel, ReplioAnalysisModels, _account?.DefaultAnalysis ?? "replio-smart")
        : AnalysisModel?.Trim() ?? string.Empty;

    /// <summary>X-Replio-Session goes with requests made while a recording runs.</summary>
    private string? RecordingSessionId => IsCapturing ? _currentSessionId : null;

    private LlmEndpoint BuildLlmEndpoint()
    {
        if (IsManagedAi)
        {
            return new LlmEndpoint
            {
                BaseUrl = ReplioApi.BaseUrl,
                ApiKey = _replioKey,
                EndpointPath = "/chat/completions",
                ApiKeyHeaderName = "Authorization",
                ApiKeyPrefix = "Bearer",
                ExtraHeaders = ReplioApi.Headers(_settings.ReplioDeviceId, RecordingSessionId)
            };
        }

        return new LlmEndpoint
        {
            BaseUrl = OpenAiBaseUrl.Trim(),
            ApiKey = ResolveLlmApiKey(),
            EndpointPath = ChatCompletionsPath,
            ApiKeyHeaderName = ApiKeyHeaderName,
            ApiKeyPrefix = ApiKeyPrefix
        };
    }

    /// <summary>API recognition is the only way on plans without local whisper.</summary>
    private bool EffectiveUseApiStt => !CanUseLocalStt || UseApiStt;

    /// <summary>Address, model, key and extra headers of API speech recognition.</summary>
    private (string Url, string Model, string Key, string Headers) ResolveSttApi(string? sessionId)
    {
        if (IsManagedAi)
        {
            return (
                ReplioApi.BaseUrl,
                EffectiveReplioModel(_settings.ReplioSttModel, ReplioSttModels, _account?.DefaultStt ?? "replio-speech"),
                _replioKey,
                ReplioApi.HeadersLine(_settings.ReplioDeviceId, sessionId));
        }

        return (
            string.IsNullOrWhiteSpace(SttApiBaseUrl) ? AppSettings.PolzaBaseUrl : SttApiBaseUrl.Trim(),
            SttApiModel.Trim(),
            ResolveSttApiKey(),
            string.Empty);
    }

    private void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            AppendLog("[account] Browser could not be opened: " + ex.Message);
        }
    }
}
