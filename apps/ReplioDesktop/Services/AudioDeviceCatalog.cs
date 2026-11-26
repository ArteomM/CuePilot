using System.Diagnostics;
using System.Text;

namespace ReplioDesktop.Services;

/// <summary>A microphone or an output device as DesktopCapture «--list-devices» prints it.</summary>
public sealed record AudioDeviceInfo(bool IsInput, bool IsDefault, string Id, string Name);

/// <summary>
/// The microphones and output devices for Settings → «Речь». Asked from DesktopCapture (it already talks to the
/// sound system: WASAPI on Windows, the replio-audio helper on macOS), so the app itself needs no audio library.
/// </summary>
public static class AudioDeviceCatalog
{
    public static async Task<IReadOnlyList<AudioDeviceInfo>> ListAsync(string workingDirectory)
    {
        string? exe = CaptureOrchestratorService.FindCaptureExecutable(workingDirectory);
        var start = new ProcessStartInfo
        {
            FileName = exe ?? "dotnet",
            Arguments = exe != null ? "--list-devices" : PlatformPaths.CaptureDotnetRunPrefix + "--list-devices",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        using Process? process = Process.Start(start);
        if (process == null)
        {
            return [];
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        var devices = new List<AudioDeviceInfo>();
        foreach (string line in output.Split('\n'))
        {
            string[] parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length >= 5 && parts[0] == "[device]" && parts[3].Length > 0)
            {
                devices.Add(new AudioDeviceInfo(parts[1] == "input", parts[2] == "1", parts[3], parts[4].Trim()));
            }
        }

        return devices;
    }
}
