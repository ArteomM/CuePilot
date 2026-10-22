using CuePilotDesktop.Models;

namespace CuePilotDesktop.Services;

public interface ISessionStore
{
    IReadOnlyList<SessionSummary> LoadSessions();
    byte[] Serialize(SessionSummary session);
    void WriteSession(string sessionId, byte[] utf8Json);
    void DeleteSession(string sessionId);
}
