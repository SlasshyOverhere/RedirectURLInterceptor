namespace RedirectUrlInterceptor;

internal sealed class RecentUrlDeduper
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _window;

    public RecentUrlDeduper(TimeSpan window)
    {
        _window = window;
    }

    public bool ShouldCapture(string url)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            CleanupExpired(now);
            if (_seen.TryGetValue(url, out var seenAt) && (now - seenAt) <= _window)
            {
                return false;
            }

            _seen[url] = now;
            return true;
        }
    }

    private void CleanupExpired(DateTimeOffset now)
    {
        if (_seen.Count == 0)
        {
            return;
        }

        var threshold = now - _window;
        var expired = _seen
            .Where(kv => kv.Value < threshold)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in expired)
        {
            _seen.Remove(key);
        }
    }
}
