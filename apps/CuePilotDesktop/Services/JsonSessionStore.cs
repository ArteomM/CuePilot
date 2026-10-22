using System.Text.Json;
using CuePilotDesktop.Models;
using System.IO;

namespace CuePilotDesktop.Services;

/// <summary>
/// Stores every session in its own file (%AppData%\CuePilot\sessions\{id}.json), so saving the active
/// session costs O(session) instead of rewriting the whole history. The legacy single-file
/// sessions.json is migrated on first load.
/// </summary>
public class JsonSessionStore : ISessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly string _rootDirectory;
    private readonly string _sessionsDirectory;
    private readonly string _legacyStorePath;

    public JsonSessionStore()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _rootDirectory = Path.Combine(appData, "CuePilot");
        _sessionsDirectory = Path.Combine(_rootDirectory, "sessions");
        _legacyStorePath = Path.Combine(_rootDirectory, "sessions.json");
        Directory.CreateDirectory(_sessionsDirectory);
    }

    public IReadOnlyList<SessionSummary> LoadSessions()
    {
        MigrateLegacyStore();

        var sessions = new List<SessionSummary>();
        foreach (string path in Directory.EnumerateFiles(_sessionsDirectory, "*.json"))
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                SessionSummary? session = JsonSerializer.Deserialize<SessionSummary>(stream, JsonOptions);
                if (session != null && !string.IsNullOrWhiteSpace(session.Id))
                {
                    sessions.Add(session);
                }
            }
            catch
            {
                // A broken file must not take the whole history down; skip it.
            }
        }

        return sessions.OrderByDescending(s => s.StartedAt).ToList();
    }

    public byte[] Serialize(SessionSummary session)
    {
        return JsonSerializer.SerializeToUtf8Bytes(session, JsonOptions);
    }

    public void WriteSession(string sessionId, byte[] utf8Json)
    {
        string path = GetSessionPath(sessionId);
        string tempPath = path + ".tmp";
        File.WriteAllBytes(tempPath, utf8Json);
        File.Move(tempPath, path, overwrite: true);
    }

    public void DeleteSession(string sessionId)
    {
        string path = GetSessionPath(sessionId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string GetSessionPath(string sessionId)
    {
        string safeId = string.Concat(sessionId.Where(char.IsLetterOrDigit));
        return Path.Combine(_sessionsDirectory, safeId + ".json");
    }

    private void MigrateLegacyStore()
    {
        if (!File.Exists(_legacyStorePath))
        {
            return;
        }

        try
        {
            using (FileStream stream = File.OpenRead(_legacyStorePath))
            {
                List<SessionSummary> legacy = JsonSerializer.Deserialize<List<SessionSummary>>(stream, JsonOptions) ?? [];
                foreach (SessionSummary session in legacy.Where(s => !string.IsNullOrWhiteSpace(s.Id)))
                {
                    if (!File.Exists(GetSessionPath(session.Id)))
                    {
                        WriteSession(session.Id, Serialize(session));
                    }
                }
            }

            File.Move(_legacyStorePath, _legacyStorePath + ".migrated", overwrite: true);
        }
        catch
        {
            // Keep the legacy file in place; migration is retried on next start.
        }
    }
}
