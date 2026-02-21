namespace RedirectUrlInterceptor;

internal sealed record BrowserLaunchEvent(
    string ProcessName,
    int ProcessId,
    int ParentProcessId,
    string? ParentProcessName,
    string CommandLine);

internal sealed record RedirectHop(
    int Hop,
    string Url,
    int? StatusCode,
    string? Location,
    string? Error);

internal sealed record RedirectTrace(
    string OriginalUrl,
    string FinalUrl,
    IReadOnlyList<RedirectHop> Hops,
    string? Error);

internal sealed class InterceptRecord
{
    public DateTimeOffset TimestampUtc { get; init; }

    public string SourceProcess { get; init; } = string.Empty;

    public int SourcePid { get; init; }

    public string BrowserProcess { get; init; } = string.Empty;

    public int BrowserPid { get; init; }

    public string? ParentProcess { get; init; }

    public int ParentPid { get; init; }

    public string Url { get; init; } = string.Empty;

    public RedirectTrace? RedirectTrace { get; init; }
}
