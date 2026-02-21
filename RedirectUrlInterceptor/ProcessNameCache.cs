using System.Collections.Concurrent;
using System.Diagnostics;

namespace RedirectUrlInterceptor;

internal sealed class ProcessNameCache
{
    private readonly ConcurrentDictionary<int, CacheEntry> _cache = new();
    private readonly TimeSpan _ttl = TimeSpan.FromMinutes(1);

    public string? TryGetName(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (_cache.TryGetValue(processId, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Name;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            var exeName = ProcessNameHelper.ToExeName(process.ProcessName);
            _cache[processId] = new CacheEntry(exeName, now.Add(_ttl));
            return exeName;
        }
        catch
        {
            _cache[processId] = new CacheEntry(null, now.AddSeconds(10));
            return null;
        }
    }

    private sealed record CacheEntry(string? Name, DateTimeOffset ExpiresAt);
}
