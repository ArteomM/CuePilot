namespace CuePilotDesktop.ViewModels;

/// <summary>
/// One-shot style UI timer without WPF: ticks are posted to the synchronization context it was created on,
/// so handlers may touch bound properties. Same Start/Stop/Tick shape as DispatcherTimer.
/// </summary>
public sealed class UiTimer : IDisposable
{
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly Timer _timer;

    public UiTimer()
    {
        _timer = new Timer(_ => Post(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public TimeSpan Interval { get; init; }

    public event EventHandler? Tick;

    public void Start() => _timer.Change(Interval, Interval);

    public void Stop() => _timer.Change(Timeout.Infinite, Timeout.Infinite);

    public void Dispose() => _timer.Dispose();

    private void Post()
    {
        if (_context == null)
        {
            Tick?.Invoke(this, EventArgs.Empty);
            return;
        }

        _context.Post(_ => Tick?.Invoke(this, EventArgs.Empty), null);
    }
}
