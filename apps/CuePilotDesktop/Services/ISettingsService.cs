namespace CuePilotDesktop.Services;

public interface ISettingsService
{
    AppSettings Load();
    void Save(AppSettings settings);
}
