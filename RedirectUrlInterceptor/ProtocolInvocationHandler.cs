using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace RedirectUrlInterceptor;

internal static class ProtocolInvocationHandler
{
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
            WriteCapture(url, settings.ForwardBrowserPath, settings.ForwardInterceptedLinksToBrowser, logger);
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

    private static void WriteCapture(string url, string? browserPath, bool forwarded, FileLogger logger)
    {
        try
        {
            using var writer = new JsonlCaptureWriter(AppPaths.LogsDirectory, logger);

            var parentPid = TryGetParentProcessId(Environment.ProcessId);
            var parentName = parentPid > 0 ? TryGetProcessName(parentPid) : null;

            writer.Write(new InterceptRecord
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                SourceProcess = ProcessNameHelper.ToExeName(parentName),
                SourcePid = parentPid,
                BrowserProcess = forwarded && !string.IsNullOrWhiteSpace(browserPath) ? Path.GetFileName(browserPath) : string.Empty,
                BrowserPid = 0,
                ParentProcess = ProcessNameHelper.ToExeName(parentName),
                ParentPid = parentPid,
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
