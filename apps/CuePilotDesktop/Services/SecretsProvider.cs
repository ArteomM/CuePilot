using System.IO;

namespace CuePilotDesktop.Services;

/// <summary>
/// Resolves API keys that are not stored in settings.json: environment variables first,
/// then a local key=value file: secrets.txt in the project root, next to the executable,
/// or in %AppData%\CuePilot (the place for an installed copy).
/// Keys are never logged.
/// </summary>
public sealed class SecretsProvider
{
    private const string SecretsFileName = "secrets.txt";

    private readonly IReadOnlyList<string> _candidatePaths;

    public SecretsProvider(string projectRoot)
    {
        _candidatePaths =
        [
            Path.Combine(projectRoot, SecretsFileName),
            Path.Combine(AppContext.BaseDirectory, SecretsFileName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CuePilot", SecretsFileName)
        ];
    }

    public string GetLlmApiKey()
    {
        return FirstNonEmpty(
            Environment.GetEnvironmentVariable("POLZA_API_KEY"),
            Environment.GetEnvironmentVariable("CUEPILOT_API_KEY"),
            ReadValue("ApiKey"));
    }

    public string GetSttApiKey()
    {
        return FirstNonEmpty(
            Environment.GetEnvironmentVariable("CUEPILOT_STT_API_KEY"),
            ReadValue("SttApiKey"));
    }

    private string ReadValue(string key)
    {
        foreach (string path in _candidatePaths)
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                foreach (string line in File.ReadLines(path))
                {
                    int separator = line.IndexOf('=');
                    if (separator <= 0)
                    {
                        continue;
                    }

                    string name = line[..separator].Trim();
                    if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
                    {
                        return line[(separator + 1)..].Trim();
                    }
                }
            }
            catch
            {
                // Unreadable secrets file is treated as missing.
            }
        }

        return string.Empty;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;
    }
}
