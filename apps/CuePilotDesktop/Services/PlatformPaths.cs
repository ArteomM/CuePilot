using System.IO;

namespace CuePilotDesktop.Services;

/// <summary>
/// File names that differ between Windows and the macOS app bundle. On macOS DesktopCapture, whisper-cli and
/// whisper-server sit next to CuePilot in CuePilot.app/Contents/MacOS.
/// </summary>
public static class PlatformPaths
{
    public static string CaptureExecutableName => OperatingSystem.IsWindows() ? "DesktopCapture.exe" : "DesktopCapture";

    /// <summary>Target framework of a DesktopCapture build in a source checkout (bin/Debug/&lt;tfm&gt;).</summary>
    public static string CaptureTargetFramework => OperatingSystem.IsWindows() ? "net8.0-windows" : "net8.0";

    /// <summary>"dotnet run" arguments for a source checkout without a built DesktopCapture.</summary>
    public static string CaptureDotnetRunPrefix => OperatingSystem.IsWindows() ? "run -- " : "run -p:CuePilotMac=true -- ";

    public static string DefaultWhisperBin(string projectRoot) => OperatingSystem.IsWindows()
        ? Path.Combine(projectRoot, "tools", "whisper.cpp", "Release", "whisper-cli.exe")
        : Path.Combine(projectRoot, "whisper-cli");
}
