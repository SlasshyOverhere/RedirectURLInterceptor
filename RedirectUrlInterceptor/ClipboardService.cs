using System.Collections.Concurrent;
using System.Windows.Forms;

namespace RedirectUrlInterceptor;

internal sealed class ClipboardService : IDisposable
{
    private readonly FileLogger _logger;
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _workerThread;

    private string? _lastQueued;
    private bool _disposed;

    public ClipboardService(FileLogger logger)
    {
        _logger = logger;
        _workerThread = new Thread(RunWorker)
        {
            IsBackground = true,
            Name = "ClipboardServiceWorker"
        };
        _workerThread.SetApartmentState(ApartmentState.STA);
        _workerThread.Start();
    }

    public void TrySetText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || _disposed)
        {
            return;
        }

        // Skip redundant enqueue if same URL is already the newest queued value.
        if (string.Equals(_lastQueued, text, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastQueued = text;
        _queue.Enqueue(text);
        _signal.Set();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _signal.Set();
        _workerThread.Join(500);
        _signal.Dispose();
        _cts.Dispose();
    }

    private void RunWorker()
    {
        while (!_cts.IsCancellationRequested)
        {
            _signal.WaitOne(500);

            while (_queue.TryDequeue(out var text))
            {
                TrySetClipboardWithRetries(text);
            }
        }
    }

    private void TrySetClipboardWithRetries(string text)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch
            {
                Thread.Sleep(25 * attempt);
            }
        }

        _logger.Info("Clipboard busy; skipped one URL copy.");
    }
}
