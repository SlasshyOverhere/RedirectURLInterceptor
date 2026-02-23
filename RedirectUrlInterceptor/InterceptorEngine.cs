using System.Collections.Immutable;
using System.Management;

namespace RedirectUrlInterceptor;

internal sealed class InterceptorEngine : IDisposable
{
    private readonly JsonlCaptureWriter _captureWriter;
    private readonly FileLogger _logger;
    private readonly ProcessNameCache _processNameCache = new();
    private readonly ClipboardService _clipboardService;
    private readonly RecentUrlDeduper _deduper = new(TimeSpan.FromSeconds(10));
    private readonly object _stateGate = new();
    private readonly object _resolverGate = new();

    private ImmutableHashSet<string> _excludedParents =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);

    private ImmutableHashSet<string> _browserProcesses =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, AppSettings.DefaultBrowserProcesses);

    private bool _resolveRedirects;
    private int _redirectMaxHops = 8;
    private int _redirectTimeoutSeconds = 6;

    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private RedirectResolver? _redirectResolver;

    public event Action<InterceptRecord>? RecordCaptured;

    public InterceptorEngine(JsonlCaptureWriter captureWriter, FileLogger logger)
    {
        _captureWriter = captureWriter;
        _logger = logger;
        _clipboardService = new ClipboardService(_logger);
    }

    public bool IsRunning
    {
        get
        {
            lock (_stateGate)
            {
                return _runTask is { IsCompleted: false };
            }
        }
    }

    public void ApplySettings(AppSettings settings)
    {
        settings.Normalize();

        Volatile.Write(
            ref _excludedParents,
            settings.ExcludedParentProcesses
                .Select(ProcessNameHelper.Normalize)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase));

        Volatile.Write(
            ref _browserProcesses,
            settings.BrowserProcesses
                .Select(ProcessNameHelper.Normalize)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase));

        _resolveRedirects = settings.ResolveRedirects;
        _redirectMaxHops = settings.RedirectMaxHops;
        _redirectTimeoutSeconds = settings.RedirectTimeoutSeconds;

        lock (_resolverGate)
        {
            _redirectResolver?.Dispose();
            _redirectResolver = null;
        }
    }

    public void Start()
    {
        lock (_stateGate)
        {
            if (_runTask is { IsCompleted: false })
            {
                return;
            }

            _runCts = new CancellationTokenSource();
            _runTask = Task.Run(() => RunMonitorLoopAsync(_runCts.Token), CancellationToken.None);
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? runTask;

        lock (_stateGate)
        {
            cts = _runCts;
            runTask = _runTask;
            _runCts = null;
            _runTask = null;
        }

        if (cts is null || runTask is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
            await runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        catch (Exception ex)
        {
            _logger.Error("Unexpected exception while stopping interceptor.", ex);
        }
        finally
        {
            cts.Dispose();
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();

        lock (_resolverGate)
        {
            _redirectResolver?.Dispose();
            _redirectResolver = null;
        }

        _clipboardService.Dispose();
    }

    private async Task RunMonitorLoopAsync(CancellationToken cancellationToken)
    {
        var wmiUnavailable = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!wmiUnavailable)
                {
                    using var wmiMonitor = new BrowserLaunchMonitor(_processNameCache);
                    await wmiMonitor.RunAsync(evt => OnProcessLaunchAsync(evt, cancellationToken), cancellationToken).ConfigureAwait(false);
                    break;
                }

                using var pollingMonitor = new ProcessPollingMonitor(_processNameCache);
                await pollingMonitor.RunAsync(evt => OnProcessLaunchAsync(evt, cancellationToken), cancellationToken).ConfigureAwait(false);

                break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ManagementException ex) when (ex.ErrorCode is ManagementStatus.AccessDenied or ManagementStatus.InvalidQuery or ManagementStatus.InvalidParameter)
            {
                _logger.Error("WMI monitor unavailable. Switching to polling monitor.", ex);
                wmiUnavailable = true;
            }
            catch (Exception ex)
            {
                _logger.Error("Monitor loop failed. Restarting in 3 seconds.", ex);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task OnProcessLaunchAsync(BrowserLaunchEvent evt, CancellationToken cancellationToken)
    {
        try
        {
            var sourceProcessNormalized = ProcessNameHelper.Normalize(evt.ProcessName);
            var parentProcessNormalized = ProcessNameHelper.Normalize(evt.ParentProcessName);

            if (IsExcluded(sourceProcessNormalized, parentProcessNormalized, evt.ParentProcessId))
            {
                return;
            }

            var browserSnapshot = Volatile.Read(ref _browserProcesses);
            var sourceIsBrowser = browserSnapshot.Contains(sourceProcessNormalized);

            foreach (var url in CommandLineUrlExtractor.Extract(evt.CommandLine))
            {
                if (!_deduper.ShouldCapture(url))
                {
                    continue;
                }

                _clipboardService.TrySetText(url);

                RedirectTrace? redirectTrace = null;
                if (_resolveRedirects)
                {
                    var resolver = GetOrCreateResolver();
                    redirectTrace = await resolver.ResolveAsync(url, cancellationToken).ConfigureAwait(false);
                }

                var record = new InterceptRecord
                {
                    TimestampUtc = DateTimeOffset.UtcNow,
                    SourceProcess = ProcessNameHelper.ToExeName(evt.ProcessName),
                    SourcePid = evt.ProcessId,
                    BrowserProcess = sourceIsBrowser ? ProcessNameHelper.ToExeName(evt.ProcessName) : string.Empty,
                    BrowserPid = sourceIsBrowser ? evt.ProcessId : 0,
                    ParentProcess = ProcessNameHelper.ToExeName(evt.ParentProcessName),
                    ParentPid = evt.ParentProcessId,
                    Url = url,
                    RedirectTrace = redirectTrace
                };

                _captureWriter.Write(record);
                RecordCaptured?.Invoke(record);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Error("Failed while processing browser launch.", ex);
        }
    }

    private bool IsExcluded(string sourceProcessName, string parentProcessName, int parentProcessId)
    {
        var excludedSnapshot = Volatile.Read(ref _excludedParents);
        if (excludedSnapshot.Count == 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(sourceProcessName) && excludedSnapshot.Contains(sourceProcessName))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(parentProcessName) && excludedSnapshot.Contains(parentProcessName))
        {
            return true;
        }

        // Walk a few levels up to catch launcher/helper chains.
        var currentPid = parentProcessId;
        var visited = new HashSet<int>();
        for (var depth = 0; depth < 3 && currentPid > 0 && visited.Add(currentPid); depth++)
        {
            currentPid = TryGetParentProcessId(currentPid);
            if (currentPid <= 0)
            {
                break;
            }

            var ancestorName = ProcessNameHelper.Normalize(_processNameCache.TryGetName(currentPid));
            if (!string.IsNullOrWhiteSpace(ancestorName) && excludedSnapshot.Contains(ancestorName))
            {
                return true;
            }
        }

        return false;
    }

    private static int TryGetParentProcessId(int processId)
    {
        if (processId <= 0)
        {
            return 0;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {processId}");
            using var results = searcher.Get();
            foreach (ManagementObject result in results)
            {
                var value = result["ParentProcessId"];
                if (value is not null)
                {
                    return Convert.ToInt32(value);
                }
            }
        }
        catch
        {
            return 0;
        }

        return 0;
    }

    private RedirectResolver GetOrCreateResolver()
    {
        lock (_resolverGate)
        {
            if (_redirectResolver is null ||
                _redirectResolver.MaxHops != _redirectMaxHops ||
                _redirectResolver.TimeoutSeconds != _redirectTimeoutSeconds)
            {
                _redirectResolver?.Dispose();
                _redirectResolver = new RedirectResolver(_redirectMaxHops, _redirectTimeoutSeconds);
            }

            return _redirectResolver;
        }
    }
}
