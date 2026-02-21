using System.Globalization;
using System.Management;

namespace RedirectUrlInterceptor;

internal sealed class BrowserLaunchMonitor : IDisposable
{
    private readonly ProcessNameCache _processNameCache;
    private readonly ManagementEventWatcher _watcher;

    public BrowserLaunchMonitor(ProcessNameCache processNameCache)
    {
        _processNameCache = processNameCache;
        _watcher = new ManagementEventWatcher(
            "SELECT TargetInstance FROM __InstanceCreationEvent WITHIN 1 WHERE TargetInstance ISA 'Win32_Process'");
    }

    public async Task RunAsync(Func<BrowserLaunchEvent, Task> onBrowserLaunch, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = new HashSet<Task>();
        var gate = new object();

        EventArrivedEventHandler handler = (_, args) =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var launchEvent = TryBuildEvent(args.NewEvent);
            if (launchEvent is null)
            {
                return;
            }

            var task = Task.Run(
                async () =>
                {
                    try
                    {
                        await onBrowserLaunch(launchEvent).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Callback errors are handled by the caller.
                    }
                },
                CancellationToken.None);

            lock (gate)
            {
                inFlight.Add(task);
            }

            _ = task.ContinueWith(
                t =>
                {
                    lock (gate)
                    {
                        inFlight.Remove(t);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        };

        _watcher.EventArrived += handler;
        using var registration = cancellationToken.Register(() => completion.TrySetResult());

        try
        {
            _watcher.Start();
            await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _watcher.EventArrived -= handler;
            SafeStopWatcher();

            Task[] pending;
            lock (gate)
            {
                pending = inFlight.ToArray();
            }

            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        SafeStopWatcher();
        _watcher.Dispose();
    }

    private BrowserLaunchEvent? TryBuildEvent(ManagementBaseObject evt)
    {
        if (evt["TargetInstance"] is not ManagementBaseObject targetInstance)
        {
            return null;
        }

        var processName = targetInstance["Name"] as string ?? string.Empty;
        var processId = ReadInt(targetInstance, "ProcessId");
        var commandLine = targetInstance["CommandLine"] as string;
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            commandLine = TryGetCommandLineByPid(processId);
        }

        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var parentProcessId = ReadInt(targetInstance, "ParentProcessId");

        var parentProcessName = parentProcessId > 0
            ? _processNameCache.TryGetName(parentProcessId)
            : null;

        return new BrowserLaunchEvent(
            processName,
            processId,
            parentProcessId,
            parentProcessName,
            commandLine);
    }

    private static int ReadInt(ManagementBaseObject evt, string fieldName)
    {
        var raw = evt[fieldName];
        if (raw is null)
        {
            return 0;
        }

        return raw switch
        {
            int value => value,
            uint value => unchecked((int)value),
            long value => unchecked((int)value),
            ulong value => unchecked((int)value),
            _ => Convert.ToInt32(raw, CultureInfo.InvariantCulture)
        };
    }

    private void SafeStopWatcher()
    {
        try
        {
            _watcher.Stop();
        }
        catch
        {
            // no-op
        }
    }

    private static string? TryGetCommandLineByPid(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {processId}");
            using var results = searcher.Get();

            foreach (ManagementObject result in results)
            {
                return result["CommandLine"] as string;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }
}
