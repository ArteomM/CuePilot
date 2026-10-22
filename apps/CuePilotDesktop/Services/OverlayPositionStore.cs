using System.IO;
using System.Text.Json;

namespace CuePilotDesktop.Services;

public class OverlayPositionStore
{
    private readonly string _filePath;

    public OverlayPositionStore()
    {
        string appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CuePilot");
        Directory.CreateDirectory(appData);
        _filePath = Path.Combine(appData, "mini-overlay-position.json");
    }

    public OverlayWindowPlacement? Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<OverlayWindowPlacement>(json);
        }
        catch
        {
            return null;
        }
    }

    public void Save(OverlayWindowPlacement placement)
    {
        try
        {
            var json = JsonSerializer.Serialize(placement, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_filePath, json);
        }
        catch (IOException)
        {
            // Position is a convenience; a locked file must not crash the overlay.
        }
    }
}

public class OverlayWindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Compact { get; set; }
}
