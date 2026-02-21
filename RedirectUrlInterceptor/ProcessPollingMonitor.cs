using System.Diagnostics;
using System.Management;

namespace RedirectUrlInterceptor;

internal sealed class ProcessPollingMonitor : IDisposable
{
    private readonly ProcessNameCache _processNameCache;
    private readonly TimeSpan _pollInterval;

    public ProcessPollingMonitor(ProcessNameCache processNameCache, TimeSpan? pollInterval = null)
    {
        _processNameCache = processNameCache;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(700);
    }

    public async Task RunAsync(Func<BrowserLaunchEvent, Task> onProcessLaunch, CancellationToken cancellationToken)
    {
        var previousPids = SnapshotCurrentPids();

        while (!cancellationToken.IsCancellationRequested)
        {
            var currentPids = new HashSet<int>();

            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var pid = process.Id;
                    currentPids.Add(pid);

                    if (previousPids.Contains(pid))
                    {
                        continue;
                    }

                    var commandLine = TryGetCommandLineByPid(pid);
                    if (string.IsNullOrWhiteSpace(commandLine))
                    {
                        continue;
                    }

                    var processName = process.ProcessName + ".exe";
                    var parentPid = TryGetParentProcessId(pid);
                    var parentName = parentPid > 0 ? _processNameCache.TryGetName(parentPid) : null;

                    var evt = new BrowserLaunchEvent(
                        processName,
                        pid,
                        parentPid,
                        parentName,
                        commandLine);

                    await onProcessLaunch(evt).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore inaccessible or short-lived processes.
                }
                finally
                {
                    process.Dispose();
                }
            }

            previousPids = currentPids;

            try
            {
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
    }

    private static HashSet<int> SnapshotCurrentPids()
    {
        var set = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                set.Add(process.Id);
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return set;
    }

    private static int TryGetParentProcessId(int processId)
    {
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

    private static string? TryGetCommandLineByPid(int processId)
    {
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
