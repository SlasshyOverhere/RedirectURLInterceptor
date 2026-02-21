using System.Text;

namespace RedirectUrlInterceptor;

internal sealed class FileLogger : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;

    public FileLogger(string logPath)
    {
        var directory = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _writer = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true,
            NewLine = Environment.NewLine
        };
    }

    public void Info(string message)
    {
        Write("INFO", message, null);
    }

    public void Error(string message, Exception? exception = null)
    {
        Write("ERROR", message, exception);
    }

    private void Write(string level, string message, Exception? exception)
    {
        try
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            var builder = new StringBuilder();
            builder.Append('[').Append(now).Append("] ");
            builder.Append(level).Append(": ").Append(message);

            if (exception is not null)
            {
                builder.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
                if (!string.IsNullOrWhiteSpace(exception.StackTrace))
                {
                    builder.AppendLine();
                    builder.Append(exception.StackTrace);
                }
            }

            lock (_gate)
            {
                _writer.WriteLine(builder.ToString());
            }
        }
        catch
        {
            // Logging must not crash the app.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Dispose();
        }
    }
}
