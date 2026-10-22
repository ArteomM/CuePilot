using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;

namespace CuePilotDesktop.Services;

public class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public JsonSettingsService()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string directory = Path.Combine(appData, "CuePilot");
        Directory.CreateDirectory(directory);
        _settingsPath = Path.Combine(directory, "settings.json");
    }

    public AppSettings Load()
    {
        AppSettings settings;
        if (!File.Exists(_settingsPath))
        {
            settings = new AppSettings { SettingsVersion = AppSettings.CurrentSettingsVersion };
        }
        else
        {
            try
            {
                string json = File.ReadAllText(_settingsPath);
                settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? throw new JsonException("settings.json is empty.");
            }
            catch
            {
                // Never replace a file we could not read: keep a copy so the key and options can be restored.
                TryCopy(_settingsPath, Path.Combine(Path.GetDirectoryName(_settingsPath)!, $"settings.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.json"));
                return new AppSettings { SettingsVersion = AppSettings.CurrentSettingsVersion };
            }
        }

        // Auto-detect mixes Russian and Ukrainian in the transcript: recognition always uses a fixed language.
        if (string.IsNullOrWhiteSpace(settings.CaptureLanguage) ||
            string.Equals(settings.CaptureLanguage, "auto", StringComparison.OrdinalIgnoreCase))
        {
            settings.CaptureLanguage = "ru";
        }

        if (settings.SettingsVersion < AppSettings.CurrentSettingsVersion)
        {
            TryCopy(_settingsPath, _settingsPath + $".v{settings.SettingsVersion}.bak");
            MigrateToPolza(settings);
            settings.SettingsVersion = AppSettings.CurrentSettingsVersion;
            TrySave(settings);
        }

        return settings;
    }

    /// <summary>
    /// Writes the current settings on top of the existing file: keys this version does not know
    /// (written by a newer or older CuePilot) are kept, and the previous file is saved as settings.json.bak.
    /// </summary>
    public void Save(AppSettings settings)
    {
        JsonObject merged = ReadExistingObject() ?? new JsonObject();
        JsonObject current = JsonSerializer.SerializeToNode(settings, JsonOptions)!.AsObject();
        foreach (KeyValuePair<string, JsonNode?> property in current.ToList())
        {
            current.Remove(property.Key);
            merged[property.Key] = property.Value;
        }

        string tempPath = _settingsPath + ".tmp";
        File.WriteAllText(tempPath, merged.ToJsonString(JsonOptions));
        TryCopy(_settingsPath, _settingsPath + ".bak");
        File.Move(tempPath, _settingsPath, overwrite: true);
    }

    private JsonObject? ReadExistingObject()
    {
        try
        {
            return File.Exists(_settingsPath) ? JsonNode.Parse(File.ReadAllText(_settingsPath)) as JsonObject : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryCopy(string source, string destination)
    {
        try
        {
            if (File.Exists(source))
            {
                File.Copy(source, destination, overwrite: true);
            }
        }
        catch
        {
            // A missing backup must not block loading or saving settings.
        }
    }

    private void TrySave(AppSettings settings)
    {
        try
        {
            Save(settings);
        }
        catch
        {
            // Migration is repeated on next start if the file cannot be written now.
        }
    }

    /// <summary>v2: the cloud LLM provider moves to Polza.ai. Local presets (LM Studio, vLLM, Ollama) are kept.</summary>
    private static void MigrateToPolza(AppSettings settings)
    {
        string baseUrl = settings.OpenAiBaseUrl ?? string.Empty;
        bool isLocal = baseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
            baseUrl.Contains("127.0.0.1", StringComparison.Ordinal);
        if (isLocal)
        {
            return;
        }

        settings.ApiPresetName = AppSettings.PolzaPresetName;
        settings.ApiProvider = AppSettings.PolzaPresetName;
        settings.OpenAiBaseUrl = AppSettings.PolzaBaseUrl;
        settings.ChatCompletionsPath = "/chat/completions";
        settings.ApiKeyHeaderName = "Authorization";
        settings.ApiKeyPrefix = "Bearer";
        settings.OpenAiModel = AppSettings.PolzaFastModel;
        settings.AnalysisModel = AppSettings.PolzaAnalysisModel;

        // An OpenAI key is not valid for Polza.ai; the key then comes from secrets.txt or POLZA_API_KEY.
        if (settings.OpenAiApiKey.StartsWith("sk-", StringComparison.Ordinal))
        {
            settings.OpenAiApiKey = string.Empty;
        }
    }
}
