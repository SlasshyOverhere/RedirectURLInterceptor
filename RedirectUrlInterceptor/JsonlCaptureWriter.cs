using System.Globalization;
using System.Text.Json;

namespace RedirectUrlInterceptor;

internal sealed class JsonlCaptureWriter : IDisposable
{
    private readonly string _logsDirectory;
    private readonly object _gate = new();
    private readonly FileLogger _logger;

    private DateOnly _activeDate;
    private StreamWriter? _writer;

    public JsonlCaptureWriter(string logsDirectory, FileLogger logger)
    {
        _logsDirectory = logsDirectory;
        _logger = logger;
        Directory.CreateDirectory(_logsDirectory);
    }

    public void Write(InterceptRecord record)
    {
        try
        {
            var payload = JsonSerializer.Serialize(record);
            lock (_gate)
            {
                EnsureWriterUnlocked(DateOnly.FromDateTime(DateTime.UtcNow));
                _writer!.WriteLine(payload);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Failed writing capture record.", ex);
        }
    }

    public string GetCurrentLogPath()
    {
        lock (_gate)
        {
            EnsureWriterUnlocked(DateOnly.FromDateTime(DateTime.UtcNow));
            return BuildPathForDate(_activeDate);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void EnsureWriterUnlocked(DateOnly todayUtc)
    {
        if (_writer is not null && _activeDate == todayUtc)
        {
            return;
        }

        _writer?.Dispose();
        _activeDate = todayUtc;
        var path = BuildPathForDate(todayUtc);
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        };
    }

    private string BuildPathForDate(DateOnly date)
    {
        var suffix = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return Path.Combine(_logsDirectory, $"intercepts-{suffix}.jsonl");
    }
}
