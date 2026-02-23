using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace RedirectUrlInterceptor;

internal static class ProtocolInvocationHandler
{
    private static readonly HashSet<string> IgnoredInvokerProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",
        "cmd",
        "powershell",
        "pwsh",
        "conhost",
        "rundll32",
        "svchost",
        "dllhost",
        "wscript",
        "cscript",
        "msiexec",
        "runtimebroker",
        "taskhostw",
        "backgroundtaskhost",
        "searchhost",
        "searchapp",
        "startmenuexperiencehost",
        "shellexperiencehost",
        "applicationframehost"
    };

    public static bool TryHandle(string[] args, FileLogger logger)
    {
        var url = ExtractUrl(args);
        if (url is null)
        {
            if (args.Length > 0)
            {
                var preview = string.Join(" | ", args.Take(4));
                logger.Info($"Protocol invocation had no parseable URL. Args: {preview}");
            }
            return false;
        }

        try
        {
            var settings = AppSettings.Load(AppPaths.ConfigPath, logger);
            var sourceContext = ResolveSourceContext();
            if (IsExcluded(sourceContext.ExclusionCandidates, settings.ExcludedParentProcesses))
            {
                var sourceProcess = string.IsNullOrWhiteSpace(sourceContext.SourceProcessName)
                    ? "unknown"
                    : ProcessNameHelper.ToExeName(sourceContext.SourceProcessName);
                logger.Info($"Skipped protocol capture for excluded app: {sourceProcess}");
                return true;
            }

            if (settings.ForwardInterceptedLinksToBrowser &&
                (string.IsNullOrWhiteSpace(settings.ForwardBrowserPath) || !File.Exists(settings.ForwardBrowserPath)))
            {
                settings.ForwardBrowserPath = BrowserLocator.TryFindFirstInstalled();
                settings.Save(AppPaths.ConfigPath, logger);
            }

            if (settings.ForwardInterceptedLinksToBrowser &&
                (string.IsNullOrWhiteSpace(settings.ForwardBrowserPath) || !File.Exists(settings.ForwardBrowserPath)))
            {
                logger.Error("Cannot forward URL because no browser path is configured.");
                MessageBox.Show(
                    "No browser executable is configured.\nOpen tray app and set 'Forward Browser' first.",
                    AppIdentity.DisplayName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return true;
            }

            TrySetClipboard(url);
            WriteCapture(url, settings.ForwardBrowserPath, settings.ForwardInterceptedLinksToBrowser, sourceContext, logger);
            logger.Info($"Protocol URL intercepted and copied: {url}");
            NotificationHelper.ShowTransientInfo(AppIdentity.DisplayName, "Intercepted URL copied to clipboard.");

            if (settings.ForwardInterceptedLinksToBrowser && !string.IsNullOrWhiteSpace(settings.ForwardBrowserPath))
            {
                LaunchBrowser(settings.ForwardBrowserPath, url, logger);
            }
        }
        catch (Exception ex)
        {
            logger.Error("Protocol invocation failed.", ex);
        }

        return true;
    }

    private static string? ExtractUrl(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            var cleaned = arg.Trim().Trim('"');
            if (!Uri.TryCreate(cleaned, UriKind.Absolute, out var uri))
            {
                continue;
            }

            if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return cleaned;
            }

            var match = Regex.Match(cleaned, @"https?://[^\s""'<>]+", RegexOptions.IgnoreCase);
            if (match.Success && Uri.TryCreate(match.Value, UriKind.Absolute, out var embeddedUri))
            {
                if (string.Equals(embeddedUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(embeddedUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    return match.Value;
                }
            }
        }

        return null;
    }

    private static void LaunchBrowser(string browserPath, string url, FileLogger logger)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = browserPath,
                Arguments = $"\"{url}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            logger.Error($"Failed to launch browser '{browserPath}' with captured URL.", ex);
        }
    }

    private static SourceContext ResolveSourceContext()
    {
        var chain = BuildProcessChain(Environment.ProcessId, maxDepth: 10);
        if (chain.Count == 0)
        {
            return new SourceContext(null, 0, null, 0, []);
        }

        string selfNormalized;
        try
        {
            using var current = Process.GetCurrentProcess();
            selfNormalized = ProcessNameHelper.Normalize(current.ProcessName);
        }
        catch
        {
            selfNormalized = ProcessNameHelper.Normalize(AppIdentity.CanonicalId);
        }

        var sourceNode = chain
            .Skip(1)
            .FirstOrDefault(node => IsSourceCandidate(node.NormalizedName, selfNormalized))
            ?? chain.Skip(1).FirstOrDefault();

        ProcessChainNode? parentNode = null;
        if (sourceNode is not null)
        {
            var sourceIndex = chain.FindIndex(node => node.ProcessId == sourceNode.ProcessId);
            if (sourceIndex >= 0 && sourceIndex + 1 < chain.Count)
            {
                parentNode = chain[sourceIndex + 1];
            }
        }

        var exclusionCandidates = chain
            .Skip(1)
            .Select(node => node.NormalizedName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SourceContext(
            sourceNode?.Name,
            sourceNode?.ProcessId ?? 0,
            parentNode?.Name,
            parentNode?.ProcessId ?? 0,
            exclusionCandidates);
    }

    private static bool IsSourceCandidate(string normalizedProcessName, string selfNormalized)
    {
        if (string.IsNullOrWhiteSpace(normalizedProcessName))
        {
            return false;
        }

        if (string.Equals(normalizedProcessName, selfNormalized, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !IgnoredInvokerProcesses.Contains(normalizedProcessName);
    }

    private static List<ProcessChainNode> BuildProcessChain(int startPid, int maxDepth)
    {
        var chain = new List<ProcessChainNode>();
        var visited = new HashSet<int>();
        var currentPid = startPid;

        while (currentPid > 0 && maxDepth-- > 0 && visited.Add(currentPid))
        {
            var name = TryGetProcessName(currentPid);
            var parentPid = TryGetParentProcessId(currentPid);
            chain.Add(new ProcessChainNode(
                currentPid,
                parentPid,
                name,
                ProcessNameHelper.Normalize(name)));

            if (parentPid <= 0)
            {
                break;
            }

            currentPid = parentPid;
        }

        return chain;
    }

    private static bool IsExcluded(IEnumerable<string> candidates, IEnumerable<string> excludedProcesses)
    {
        var excluded = excludedProcesses
            .Select(ProcessNameHelper.Normalize)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (excluded.Count == 0)
        {
            return false;
        }

        foreach (var candidate in candidates)
        {
            var normalized = ProcessNameHelper.Normalize(candidate);
            if (!string.IsNullOrWhiteSpace(normalized) && excluded.Contains(normalized))
            {
                return true;
            }
        }

        return false;
    }

    private static void WriteCapture(string url, string? browserPath, bool forwarded, SourceContext sourceContext, FileLogger logger)
    {
        try
        {
            using var writer = new JsonlCaptureWriter(AppPaths.LogsDirectory, logger);

            writer.Write(new InterceptRecord
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                SourceProcess = ProcessNameHelper.ToExeName(sourceContext.SourceProcessName),
                SourcePid = sourceContext.SourcePid,
                BrowserProcess = forwarded && !string.IsNullOrWhiteSpace(browserPath) ? Path.GetFileName(browserPath) : string.Empty,
                BrowserPid = 0,
                ParentProcess = ProcessNameHelper.ToExeName(sourceContext.ParentProcessName),
                ParentPid = sourceContext.ParentPid,
                Url = url
            });
        }
        catch (Exception ex)
        {
            logger.Error("Failed writing protocol capture.", ex);
        }
    }

    private static int TryGetParentProcessId(int pid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {pid}");
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

    private static string? TryGetProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private sealed record SourceContext(
        string? SourceProcessName,
        int SourcePid,
        string? ParentProcessName,
        int ParentPid,
        IReadOnlyList<string> ExclusionCandidates);

    private sealed record ProcessChainNode(
        int ProcessId,
        int ParentProcessId,
        string? Name,
        string NormalizedName);

    private static void TrySetClipboard(string url)
    {
        for (var i = 1; i <= 4; i++)
        {
            try
            {
                Clipboard.SetText(url);
                return;
            }
            catch
            {
                Thread.Sleep(30 * i);
            }
        }
    }
}
