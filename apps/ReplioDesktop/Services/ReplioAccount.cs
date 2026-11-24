using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ReplioDesktop.Services;

/// <summary>
/// The Replio gateway (contract: replio/gateway-api-contract.md). Every AI and speech request of a Replio user goes
/// there with the device key; the gateway maps the key to the provider account (Polza.ai for now), checks the plan
/// and counts the minutes. The user never sees a provider key.
/// </summary>
public static class ReplioApi
{
    public const string DefaultBaseUrl = "https://api.replio.ru/v1";

    /// <summary>REPLIO_API_BASE overrides the address (a test gateway).</summary>
    public static string BaseUrl
    {
        get
        {
            string? custom = Environment.GetEnvironmentVariable("REPLIO_API_BASE");
            return (string.IsNullOrWhiteSpace(custom) ? DefaultBaseUrl : custom.Trim()).TrimEnd('/');
        }
    }

    public static string Platform => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsAndroid() ? "android" : "other";

    public static string AppVersion => typeof(ReplioApi).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>X-Replio-Device, X-Replio-App and, during a recording, X-Replio-Session.</summary>
    public static IReadOnlyList<(string Name, string Value)> Headers(string deviceId, string? sessionId)
    {
        var headers = new List<(string, string)>
        {
            ("X-Replio-Device", deviceId),
            ("X-Replio-App", Platform + "/" + AppVersion)
        };
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            headers.Add(("X-Replio-Session", sessionId));
        }

        return headers;
    }

    /// <summary>Same headers as one line for DesktopCapture: «Name=Value;Name=Value».</summary>
    public static string HeadersLine(string deviceId, string? sessionId) =>
        string.Join(";", Headers(deviceId, sessionId).Select(h => h.Name + "=" + h.Value));
}

/// <summary>A refusal of the gateway: {"error":{"code","message","resets_at","upgrade_url"}}. Message is Russian, for the user.</summary>
public sealed class ReplioApiException : Exception
{
    public ReplioApiException(int status, string code, string message, string? upgradeUrl) : base(message)
    {
        Status = status;
        Code = code;
        UpgradeUrl = upgradeUrl;
    }

    public int Status { get; }
    public string Code { get; }
    public string? UpgradeUrl { get; }

    /// <summary>The key is wrong or was revoked: the user has to sign in again.</summary>
    public bool IsSignedOut => Status == 401;

    /// <summary>Plan, quota or device limits: worth re-reading /account.</summary>
    public bool AffectsAccount => Status is 401 or 402 or 403;

    public static ReplioApiException? TryParse(int status, string body)
    {
        if (string.IsNullOrWhiteSpace(body) || !body.TrimStart().StartsWith('{'))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("error", out JsonElement error) || error.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string code = Json.Text(error, "code") ?? string.Empty;
            string? message = Json.Text(error, "message");
            // Only the gateway sends codes in this form; other providers' errors keep the generic handling.
            if (string.IsNullOrWhiteSpace(message) || code.Length == 0 || code.Contains(' '))
            {
                return null;
            }

            return new ReplioApiException(status, code, message, Json.Text(error, "upgrade_url"));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record ReplioModelOption(string Id, string Title, string Note)
{
    public string Display => string.IsNullOrWhiteSpace(Note) ? Title : Title + " · " + Note;

    public override string ToString() => Display;
}

/// <summary>GET /account: plan, what it allows, the prepared models and the minutes left.</summary>
public sealed class ReplioAccountInfo
{
    public string PlanId { get; init; } = string.Empty;
    public string PlanName { get; init; } = string.Empty;

    /// <summary>Local whisper («Локальный», «Приватный»).</summary>
    public bool LocalStt { get; init; }

    /// <summary>Own LLM providers and keys («Приватный»).</summary>
    public bool CustomProviders { get; init; }

    /// <summary>A choice between 2–3 prepared models («Про», «Команда», «Локальный»).</summary>
    public bool ModelChoice { get; init; }

    public IReadOnlyList<ReplioModelOption> FastModels { get; init; } = [];
    public IReadOnlyList<ReplioModelOption> AnalysisModels { get; init; } = [];
    public IReadOnlyList<ReplioModelOption> SttModels { get; init; } = [];
    public string DefaultFast { get; init; } = "replio-fast";
    public string DefaultAnalysis { get; init; } = "replio-smart";
    public string DefaultStt { get; init; } = "replio-speech";

    public int? MinutesTotal { get; init; }
    public int? MinutesLeft { get; init; }
    public int? AiLeftPercent { get; init; }
    public DateTime? PeriodEnd { get; init; }
    public int? DevicesMax { get; init; }
    public int? DevicesUsed { get; init; }
    public string UpgradeUrl { get; init; } = "https://replio.ru/account/plan";

    /// <summary>The JSON as received, cached in settings so the app knows the plan before the network answers.</summary>
    public string RawJson { get; init; } = string.Empty;

    public static ReplioAccountInfo Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("account", out JsonElement nested) && nested.ValueKind == JsonValueKind.Object)
        {
            root = nested;
        }

        JsonElement plan = Json.Object(root, "plan");
        JsonElement features = Json.Object(root, "features");
        JsonElement models = Json.Object(root, "models");
        JsonElement defaults = Json.Object(root, "defaults");
        JsonElement quota = Json.Object(root, "quota");
        string? periodEnd = Json.Text(quota, "period_end");
        return new ReplioAccountInfo
        {
            PlanId = Json.Text(plan, "id") ?? string.Empty,
            PlanName = Json.Text(plan, "name") ?? Json.Text(plan, "id") ?? string.Empty,
            LocalStt = Json.Bool(features, "local_stt"),
            CustomProviders = Json.Bool(features, "custom_providers"),
            ModelChoice = Json.Bool(features, "model_choice"),
            FastModels = Models(models, "fast"),
            AnalysisModels = Models(models, "analysis"),
            SttModels = Models(models, "stt"),
            DefaultFast = Json.Text(defaults, "fast") ?? "replio-fast",
            DefaultAnalysis = Json.Text(defaults, "analysis") ?? "replio-smart",
            DefaultStt = Json.Text(defaults, "stt") ?? "replio-speech",
            MinutesTotal = Json.Int(quota, "minutes_total"),
            MinutesLeft = Json.Int(quota, "minutes_left"),
            AiLeftPercent = Json.Int(quota, "ai_left_percent"),
            PeriodEnd = DateTime.TryParse(periodEnd, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime end) ? end : null,
            DevicesMax = Json.Int(quota, "devices_max"),
            DevicesUsed = Json.Int(quota, "devices_used"),
            UpgradeUrl = Json.Text(root, "upgrade_url") ?? "https://replio.ru/account/plan",
            RawJson = root.GetRawText()
        };
    }

    private static IReadOnlyList<ReplioModelOption> Models(JsonElement models, string name)
    {
        if (models.ValueKind != JsonValueKind.Object || !models.TryGetProperty(name, out JsonElement list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return list.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object && Json.Text(item, "id") is { Length: > 0 })
            .Select(item => new ReplioModelOption(Json.Text(item, "id")!, Json.Text(item, "title") ?? Json.Text(item, "id")!, Json.Text(item, "note") ?? string.Empty))
            .ToList();
    }
}

/// <summary>Device sign-in: the code to confirm on replio.ru and how often to ask whether it was confirmed.</summary>
public sealed record ReplioDeviceLogin(string DeviceCode, string UserCode, string VerificationUrl, TimeSpan ExpiresIn, TimeSpan Interval);

public enum ReplioLoginState
{
    Pending,
    Done,
    Expired,
    Denied
}

public sealed record ReplioLoginPoll(ReplioLoginState State, string Key = "", ReplioAccountInfo? Account = null, string Message = "");

/// <summary>HTTP calls of the account part of the gateway. All of them run off the UI thread.</summary>
public sealed class ReplioClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly string _deviceId;

    public ReplioClient(string deviceId)
    {
        _deviceId = deviceId;
    }

    public async Task<ReplioAccountInfo> GetAccountAsync(string key, CancellationToken token)
    {
        using HttpRequestMessage request = Build(HttpMethod.Get, "/account", key, null);
        (int status, string body) = await SendAsync(request, token).ConfigureAwait(false);
        EnsureSuccess(status, body);
        return ReplioAccountInfo.Parse(body);
    }

    public async Task<ReplioDeviceLogin> StartLoginAsync(string deviceName, CancellationToken token)
    {
        using HttpRequestMessage request = Build(HttpMethod.Post, "/auth/device/start", null, new
        {
            device_id = _deviceId,
            device_name = deviceName,
            platform = ReplioApi.Platform,
            app_version = ReplioApi.AppVersion
        });
        (int status, string body) = await SendAsync(request, token).ConfigureAwait(false);
        EnsureSuccess(status, body);
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        return new ReplioDeviceLogin(
            Json.Text(root, "device_code") ?? throw new InvalidDataException("device_code is missing"),
            Json.Text(root, "user_code") ?? string.Empty,
            Json.Text(root, "verification_url") ?? "https://replio.ru/connect",
            TimeSpan.FromSeconds(Json.Int(root, "expires_in") ?? 600),
            TimeSpan.FromSeconds(Math.Max(2, Json.Int(root, "interval") ?? 5)));
    }

    public async Task<ReplioLoginPoll> PollLoginAsync(string deviceCode, CancellationToken token)
    {
        using HttpRequestMessage request = Build(HttpMethod.Post, "/auth/device/token", null, new { device_code = deviceCode });
        (int status, string body) = await SendAsync(request, token).ConfigureAwait(false);
        if (status == 200)
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            string key = Json.Text(root, "key") ?? throw new InvalidDataException("key is missing");
            ReplioAccountInfo? account = root.TryGetProperty("account", out JsonElement acc) && acc.ValueKind == JsonValueKind.Object
                ? ReplioAccountInfo.Parse(acc.GetRawText())
                : null;
            return new ReplioLoginPoll(ReplioLoginState.Done, key, account);
        }

        ReplioApiException? error = ReplioApiException.TryParse(status, body);
        return (status, error?.Code) switch
        {
            (428, _) or (_, "authorization_pending") or (_, "slow_down") => new ReplioLoginPoll(ReplioLoginState.Pending),
            (410, _) or (_, "expired_token") => new ReplioLoginPoll(ReplioLoginState.Expired, Message: error?.Message ?? string.Empty),
            _ => new ReplioLoginPoll(ReplioLoginState.Denied, Message: error?.Message ?? ("HTTP " + status))
        };
    }

    public async Task LogoutAsync(string key, CancellationToken token)
    {
        using HttpRequestMessage request = Build(HttpMethod.Post, "/auth/logout", key, new { });
        await SendAsync(request, token).ConfigureAwait(false);
    }

    private HttpRequestMessage Build(HttpMethod method, string path, string? key, object? payload)
    {
        var request = new HttpRequestMessage(method, ReplioApi.BaseUrl + path);
        request.Headers.AcceptLanguage.ParseAdd("ru");
        foreach ((string name, string value) in ReplioApi.Headers(_deviceId, null))
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (!string.IsNullOrWhiteSpace(key))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        if (payload != null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static async Task<(int Status, string Body)> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        return ((int)response.StatusCode, body);
    }

    private static void EnsureSuccess(int status, string body)
    {
        if (status is >= 200 and < 300)
        {
            return;
        }

        throw (Exception?)ReplioApiException.TryParse(status, body) ??
            new HttpRequestException("HTTP " + status + (status == (int)HttpStatusCode.NotFound ? " (Replio gateway not found)" : string.Empty));
    }
}

/// <summary>
/// The device key, never shown to the user. Windows: encrypted with DPAPI for the current user in
/// %AppData%\Replio\replio.key. Other platforms: the same file readable only by the user (Keychain/Keystore later).
/// REPLIO_KEY overrides it (testing against a gateway).
/// </summary>
public static class ReplioKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Replio device key v1");

    private static string KeyPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Replio", "replio.key");

    public static string Load()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable("REPLIO_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim();
        }

        try
        {
            if (!File.Exists(KeyPath))
            {
                return string.Empty;
            }

            byte[] stored = File.ReadAllBytes(KeyPath);
            byte[] plain = OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(stored, Entropy, DataProtectionScope.CurrentUser)
                : stored;
            return Encoding.UTF8.GetString(plain).Trim();
        }
        catch
        {
            // A key from another Windows user or a damaged file: the user signs in again.
            return string.Empty;
        }
    }

    public static void Save(string key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        byte[] plain = Encoding.UTF8.GetBytes(key.Trim());
        byte[] stored = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser)
            : plain;
        File.WriteAllBytes(KeyPath, stored);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(KeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public static void Delete()
    {
        try
        {
            File.Delete(KeyPath);
        }
        catch
        {
        }
    }
}

internal static class Json
{
    public static JsonElement Object(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object ? value : default;

    public static string? Text(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static bool Bool(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    public static int? Int(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : null;
}
