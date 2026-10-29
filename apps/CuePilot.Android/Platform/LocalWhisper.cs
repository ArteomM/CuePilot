using System.IO;
using System.Runtime.InteropServices;

namespace CuePilotDesktop.Services;

/// <summary>
/// whisper.cpp inside the APK (native/build-android.sh): libcuepilot_whisper_v82.so for phones with the ARMv8.2
/// dot product and FP16, libcuepilot_whisper_v8.so for older 64-bit phones. The functions come from
/// native/cuepilot_whisper.c, which keeps whisper.cpp's large parameter structs on the C side.
/// One instance holds one loaded model; recognition runs on the caller's thread (never the UI thread).
/// </summary>
public sealed unsafe class LocalWhisper : IDisposable
{
    private static readonly object LoadGate = new();
    private static Api? _api;
    private static bool _loadTried;

    private readonly object _gate = new();
    private IntPtr _context;

    private LocalWhisper(IntPtr context)
    {
        _context = context;
    }

    /// <summary>Set by MainActivity: System.loadLibrary, which finds libraries inside the APK.</summary>
    public static Func<string, bool>? JavaLoader { get; set; }

    /// <summary>Full path of a library to use instead (tests on a computer).</summary>
    public static string? LibraryOverride { get; set; }

    /// <summary>Which library was loaded, and the CPU features whisper.cpp uses, for the log.</summary>
    public static string Description { get; private set; } = string.Empty;

    /// <summary>Why whisper.cpp is not available (a 32-bit phone, a library that did not load).</summary>
    public static string LoadError { get; private set; } = string.Empty;

    public static bool IsAvailable => GetApi() != null;

    /// <summary>Threads for recognition: the big cores of a phone.</summary>
    public static int Threads => Math.Clamp(Environment.ProcessorCount / 2, 2, 4);

    /// <summary>Loads the model; throws with a readable reason when the library or the model is missing.</summary>
    public static LocalWhisper Load(string modelPath)
    {
        Api api = GetApi() ?? throw new InvalidOperationException("whisper.cpp is not available on this phone: " + LoadError);
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
        {
            throw new FileNotFoundException("The speech model is not downloaded.", modelPath);
        }

        IntPtr path = Marshal.StringToCoTaskMemUTF8(modelPath);
        try
        {
            IntPtr context = api.Init((byte*)path);
            return context == IntPtr.Zero
                ? throw new InvalidOperationException("whisper.cpp could not load " + Path.GetFileName(modelPath) + " (damaged file or not enough memory).")
                : new LocalWhisper(context);
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }
    }

    /// <summary>Recognizes 16 kHz mono samples (-1…1). Cancelling the token stops whisper.cpp mid-piece.</summary>
    public string Transcribe(float[] samples, string language, string prompt, CancellationToken token)
    {
        Api api = _api!;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_context == IntPtr.Zero, this);
            token.ThrowIfCancellationRequested();
            IntPtr languageText = Marshal.StringToCoTaskMemUTF8(language);
            IntPtr promptText = Marshal.StringToCoTaskMemUTF8(prompt ?? string.Empty);
            try
            {
                int result;
                // Disposing the registration waits for a running abort, so the context is never freed under it.
                using (token.Register(() => api.Abort(_context)))
                {
                    fixed (float* audio = samples)
                    {
                        result = api.Transcribe(_context, audio, samples.Length, (byte*)languageText, (byte*)promptText, Threads);
                    }
                }

                token.ThrowIfCancellationRequested();
                if (result != 0)
                {
                    throw new InvalidOperationException("whisper.cpp failed with code " + result + ".");
                }

                return (Marshal.PtrToStringUTF8(api.Text(_context)) ?? string.Empty).Trim();
            }
            finally
            {
                Marshal.FreeCoTaskMem(languageText);
                Marshal.FreeCoTaskMem(promptText);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_context != IntPtr.Zero)
            {
                _api!.Free(_context);
                _context = IntPtr.Zero;
            }
        }
    }

    private static Api? GetApi()
    {
        lock (LoadGate)
        {
            if (_loadTried)
            {
                return _api;
            }

            _loadTried = true;
            try
            {
                foreach (string name in LibraryNames())
                {
                    if (LibraryOverride == null)
                    {
                        JavaLoader?.Invoke(name);
                    }

                    if (NativeLibrary.TryLoad(LibraryOverride ?? "lib" + name + ".so", out IntPtr handle))
                    {
                        _api = new Api(handle);
                        Description = (LibraryOverride ?? name) + ", " + (Marshal.PtrToStringUTF8(_api.SystemInfo()) ?? string.Empty).Trim();
                        return _api;
                    }
                }

                LoadError = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 || LibraryOverride != null
                    ? "the whisper.cpp library did not load"
                    : "local recognition needs a 64-bit phone (" + RuntimeInformation.ProcessArchitecture + ")";
            }
            catch (Exception ex)
            {
                _api = null;
                LoadError = ex.Message;
            }

            return null;
        }
    }

    private static IEnumerable<string> LibraryNames()
    {
        if (LibraryOverride != null)
        {
            yield return Path.GetFileNameWithoutExtension(LibraryOverride);
            yield break;
        }

        if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
        {
            yield break;
        }

        if (HasArmV82Features())
        {
            yield return "cuepilot_whisper_v82";
        }

        yield return "cuepilot_whisper_v8";
    }

    /// <summary>Dot product and FP16 on every core (/proc/cpuinfo «Features»): the v82 library would crash without them.</summary>
    private static bool HasArmV82Features()
    {
        try
        {
            string[] features = File.ReadLines("/proc/cpuinfo")
                .Where(line => line.StartsWith("Features", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return features.Length > 0 && features.All(line =>
            {
                var flags = line[(line.IndexOf(':') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return flags.Contains("asimddp") && flags.Contains("asimdhp") && flags.Contains("fphp");
            });
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The exports of libcuepilot_whisper.</summary>
    private sealed class Api
    {
        public readonly delegate* unmanaged<byte*, IntPtr> Init;
        public readonly delegate* unmanaged<IntPtr, float*, int, byte*, byte*, int, int> Transcribe;
        public readonly delegate* unmanaged<IntPtr, IntPtr> Text;
        public readonly delegate* unmanaged<IntPtr, void> AbortFunction;
        public readonly delegate* unmanaged<IntPtr, void> Free;
        public readonly delegate* unmanaged<IntPtr> SystemInfo;

        public Api(IntPtr library)
        {
            Init = (delegate* unmanaged<byte*, IntPtr>)NativeLibrary.GetExport(library, "cpw_init");
            Transcribe = (delegate* unmanaged<IntPtr, float*, int, byte*, byte*, int, int>)NativeLibrary.GetExport(library, "cpw_transcribe");
            Text = (delegate* unmanaged<IntPtr, IntPtr>)NativeLibrary.GetExport(library, "cpw_text");
            AbortFunction = (delegate* unmanaged<IntPtr, void>)NativeLibrary.GetExport(library, "cpw_abort");
            Free = (delegate* unmanaged<IntPtr, void>)NativeLibrary.GetExport(library, "cpw_free");
            SystemInfo = (delegate* unmanaged<IntPtr>)NativeLibrary.GetExport(library, "cpw_system_info");
        }

        public void Abort(IntPtr context) => AbortFunction(context);
    }
}
