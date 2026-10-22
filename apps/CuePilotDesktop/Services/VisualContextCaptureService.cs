using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Timers;
using System.Windows.Automation;
using CuePilotDesktop.Models;
using Timer = System.Timers.Timer;
using Forms = System.Windows.Forms;

namespace CuePilotDesktop.Services;

public sealed class VisualContextCaptureService : IVisualContextCapture, IDisposable
{
    private const int MaxStoredFrameWidth = 1280;
    private static readonly TimeSpan UiTextCacheLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UiTextTimeBudget = TimeSpan.FromMilliseconds(400);
    private static readonly ImageCodecInfo? JpegCodec = ImageCodecInfo.GetImageEncoders()
        .FirstOrDefault(c => string.Equals(c.MimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase));

    private readonly string _storageRoot;
    private readonly object _sync = new();
    private Timer? _timer;
    private string _sessionId = string.Empty;
    private ulong? _lastHash;
    private bool _isRunning;
    private IntPtr _cachedUiTextWindow;
    private string _cachedUiTextTitle = string.Empty;
    private string _cachedUiText = string.Empty;
    private DateTime _cachedUiTextAt;

    public VisualContextCaptureService()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _storageRoot = Path.Combine(appData, "CuePilot", "visual-context");
        Directory.CreateDirectory(_storageRoot);
    }

    public event EventHandler<SessionVisualFrame>? KeyFrameCaptured;
    public event EventHandler<string>? LogLine;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _isRunning;
            }
        }
    }

    public void Start(string sessionId, int intervalMs)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        lock (_sync)
        {
            StopInternal();
            _sessionId = sessionId;
            _lastHash = null;
            // AutoReset=false: the next tick is scheduled only after the current capture finished,
            // so slow captures never pile up on the thread pool.
            _timer = new Timer(Math.Clamp(intervalMs, 800, 20000));
            _timer.Elapsed += OnTimerElapsed;
            _timer.AutoReset = false;
            _timer.Start();
            _isRunning = true;
        }

        LogLine?.Invoke(this, $"[visual] Video context started for session {sessionId}.");
    }

    public void Stop()
    {
        lock (_sync)
        {
            StopInternal();
        }

        LogLine?.Invoke(this, "[visual] Video context stopped.");
    }

    public bool CaptureSnapshot(string source, bool force, out SessionVisualFrame? frame)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(_sessionId))
            {
                frame = null;
                return false;
            }

            return TryCaptureFrameLocked(source, force, out frame);
        }
    }

    public string StorageRoot => _storageRoot;

    public void DeleteFrameFiles(IEnumerable<SessionVisualFrame> frames)
    {
        foreach (SessionVisualFrame frame in frames)
        {
            TryDeleteFile(frame.FilePath);
        }
    }

    public void DeleteSessionFiles(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        try
        {
            string sessionDir = Path.Combine(_storageRoot, sessionId);
            if (Directory.Exists(sessionDir))
            {
                Directory.Delete(sessionDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    /// <summary>Removes frame folders of sessions that no longer exist and frames no session references.</summary>
    public void CleanupOrphans(IReadOnlyCollection<SessionVisualFrame> referencedFrames, IReadOnlyCollection<string> knownSessionIds)
    {
        try
        {
            var referenced = new HashSet<string>(
                referencedFrames.Select(f => f.FilePath).Where(p => !string.IsNullOrWhiteSpace(p)),
                StringComparer.OrdinalIgnoreCase);
            var sessions = new HashSet<string>(knownSessionIds, StringComparer.OrdinalIgnoreCase);
            foreach (string sessionDir in Directory.EnumerateDirectories(_storageRoot))
            {
                if (!sessions.Contains(Path.GetFileName(sessionDir)))
                {
                    Directory.Delete(sessionDir, recursive: true);
                    continue;
                }

                foreach (string file in Directory.EnumerateFiles(sessionDir, "*.jpg"))
                {
                    if (!referenced.Contains(file))
                    {
                        TryDeleteFile(file);
                    }
                }
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        SessionVisualFrame? frame = null;
        bool captured = false;
        try
        {
            lock (_sync)
            {
                if (!_isRunning)
                {
                    return;
                }

                captured = TryCaptureFrameLocked("video", false, out frame);
            }

            if (captured && frame != null)
            {
                KeyFrameCaptured?.Invoke(this, frame);
            }
        }
        catch (Exception ex)
        {
            LogLine?.Invoke(this, "[visual] Capture failed: " + ex.Message);
        }
        finally
        {
            lock (_sync)
            {
                if (_isRunning && ReferenceEquals(sender, _timer))
                {
                    _timer?.Start();
                }
            }
        }
    }

    private bool TryCaptureFrameLocked(string source, bool force, out SessionVisualFrame? frame)
    {
        frame = null;
        Rectangle bounds = Forms.Screen.PrimaryScreen?.Bounds ?? Rectangle.Empty;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            LogLine?.Invoke(this, "[visual] Frame skipped: invalid screen bounds.");
            return false;
        }

        string sessionDir = Path.Combine(_storageRoot, _sessionId);
        Directory.CreateDirectory(sessionDir);

        string fileName = $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{source}.jpg";
        string path = Path.Combine(sessionDir, fileName);
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        }

        ulong hash = ComputeAverageHash(bitmap);
        int diffScore = _lastHash.HasValue ? HammingDistance(_lastHash.Value, hash) : 64;
        if (!force && _lastHash.HasValue && diffScore <= 4)
        {
            return false;
        }

        _lastHash = hash;
        SaveDownscaledJpeg(bitmap, path, quality: 55L);

        (string windowTitle, string textSignal) = CaptureForegroundWindowSignals();
        frame = new SessionVisualFrame
        {
            Timestamp = DateTime.Now,
            FilePath = path,
            Source = source,
            ChangeReason = force ? "manual_snapshot" : "keyframe_change_detected",
            DifferenceScore = diffScore,
            WindowTitle = windowTitle,
            OcrText = textSignal
        };
        return true;
    }

    private static ulong ComputeAverageHash(Bitmap source)
    {
        using var resized = new Bitmap(8, 8, PixelFormat.Format24bppRgb);
        using (Graphics g = Graphics.FromImage(resized))
        {
            g.DrawImage(source, new Rectangle(0, 0, 8, 8));
        }

        var values = new byte[64];
        int index = 0;
        long sum = 0;
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                Color pixel = resized.GetPixel(x, y);
                byte gray = (byte)((pixel.R * 299 + pixel.G * 587 + pixel.B * 114) / 1000);
                values[index++] = gray;
                sum += gray;
            }
        }

        byte average = (byte)(sum / 64);
        ulong hash = 0;
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] >= average)
            {
                hash |= 1UL << i;
            }
        }

        return hash;
    }

    private static int HammingDistance(ulong left, ulong right)
    {
        ulong value = left ^ right;
        int count = 0;
        while (value != 0)
        {
            count++;
            value &= value - 1;
        }

        return count;
    }

    private static void SaveDownscaledJpeg(Bitmap bitmap, string path, long quality)
    {
        if (bitmap.Width <= MaxStoredFrameWidth)
        {
            SaveJpeg(bitmap, path, quality);
            return;
        }

        int height = (int)Math.Round(bitmap.Height * (MaxStoredFrameWidth / (double)bitmap.Width));
        using var scaled = new Bitmap(MaxStoredFrameWidth, Math.Max(1, height), PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(scaled))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(bitmap, new Rectangle(0, 0, scaled.Width, scaled.Height));
        }

        SaveJpeg(scaled, path, quality);
    }

    private static void SaveJpeg(Bitmap bitmap, string path, long quality)
    {
        if (JpegCodec == null)
        {
            bitmap.Save(path, ImageFormat.Jpeg);
            return;
        }

        var encoder = System.Drawing.Imaging.Encoder.Quality;
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(encoder, quality);
        bitmap.Save(path, JpegCodec, parameters);
    }

    private (string WindowTitle, string TextSignal) CaptureForegroundWindowSignals()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return (string.Empty, string.Empty);
        }

        string title = GetWindowTitle(hwnd);
        _ = GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == (uint)Environment.ProcessId)
        {
            // Walking our own window from a background thread can deadlock with the UI thread.
            return (title, string.Empty);
        }

        // UI Automation walks are expensive and make some apps (browsers, Electron) slower system-wide,
        // so the text is re-read only when the foreground window changes or the cache gets old.
        bool cacheValid = hwnd == _cachedUiTextWindow &&
            string.Equals(title, _cachedUiTextTitle, StringComparison.Ordinal) &&
            DateTime.UtcNow - _cachedUiTextAt < UiTextCacheLifetime;
        if (!cacheValid)
        {
            _cachedUiText = ExtractUiText(hwnd);
            _cachedUiTextWindow = hwnd;
            _cachedUiTextTitle = title;
            _cachedUiTextAt = DateTime.UtcNow;
        }

        return (title, _cachedUiText);
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var builder = new StringBuilder(512);
        _ = GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString().Trim();
    }

    private static string ExtractUiText(IntPtr hwnd)
    {
        try
        {
            AutomationElement root = AutomationElement.FromHandle(hwnd);
            if (root == null)
            {
                return string.Empty;
            }

            var queue = new Queue<AutomationElement>();
            queue.Enqueue(root);
            var chunks = new List<string>();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            int visited = 0;
            while (queue.Count > 0 && chunks.Count < 50 && visited < 150 && stopwatch.Elapsed < UiTextTimeBudget)
            {
                visited++;
                AutomationElement current = queue.Dequeue();
                string? name = current.Current.Name;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    string normalized = NormalizeChunk(name);
                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        chunks.Add(normalized);
                    }
                }

                AutomationElementCollection children = current.FindAll(TreeScope.Children, Condition.TrueCondition);
                for (int i = 0; i < children.Count && queue.Count < 150; i++)
                {
                    queue.Enqueue(children[i]);
                }
            }

            return string.Join(" | ", chunks.Distinct(StringComparer.OrdinalIgnoreCase).Take(18));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeChunk(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string normalized = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        while (normalized.Contains("  ", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("  ", " ", StringComparison.Ordinal);
        }

        if (normalized.Length > 80)
        {
            normalized = normalized[..80];
        }

        return normalized;
    }

    private void StopInternal()
    {
        if (_timer != null)
        {
            _timer.Stop();
            _timer.Elapsed -= OnTimerElapsed;
            _timer.Dispose();
            _timer = null;
        }

        _isRunning = false;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            StopInternal();
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
