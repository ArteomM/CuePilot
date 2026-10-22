using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace CuePilotDesktop.Services;

/// <summary>
/// Writes app log lines to %LocalAppData%\CuePilot\logs on a background thread.
/// Memory stays bounded: when the queue is full new lines are dropped instead of blocking the UI.
/// </summary>
public sealed class FileLogSink : IDisposable
{
    private const int RetentionDays = 7;

    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 5000);
    private readonly Thread _writerThread;
    private readonly string _logDirectory;

    public FileLogSink()
    {
        _logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CuePilot",
            "logs");
        Directory.CreateDirectory(_logDirectory);
        DeleteOldLogs();

        _writerThread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "CuePilot log writer"
        };
        _writerThread.Start();
    }

    public string LogDirectory => _logDirectory;

    public void Write(string line)
    {
        if (_queue.IsAddingCompleted)
        {
            return;
        }

        _queue.TryAdd($"[{DateTime.Now:HH:mm:ss}] {line}");
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writerThread.Join(TimeSpan.FromSeconds(2));
    }

    private void WriterLoop()
    {
        StreamWriter? writer = null;
        DateTime writerDate = DateTime.MinValue;
        try
        {
            foreach (string line in _queue.GetConsumingEnumerable())
            {
                try
                {
                    if (writer == null || writerDate != DateTime.Today)
                    {
                        writer?.Dispose();
                        writerDate = DateTime.Today;
                        string path = Path.Combine(_logDirectory, $"cuepilot-{writerDate:yyyyMMdd}.log");
                        writer = new StreamWriter(path, append: true, new UTF8Encoding(false));
                    }

                    writer.WriteLine(line);
                    if (_queue.Count == 0)
                    {
                        writer.Flush();
                    }
                }
                catch
                {
                    writer?.Dispose();
                    writer = null;
                }
            }
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private void DeleteOldLogs()
    {
        try
        {
            DateTime threshold = DateTime.Now.AddDays(-RetentionDays);
            foreach (string path in Directory.EnumerateFiles(_logDirectory, "cuepilot-*.log"))
            {
                if (File.GetLastWriteTime(path) < threshold)
                {
                    File.Delete(path);
                }
            }
        }
        catch
        {
            // Log cleanup is best effort.
        }
    }
}
