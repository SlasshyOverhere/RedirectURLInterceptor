using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RedirectUrlInterceptor;

internal static class ProtocolRegistrationManager
{
    private static string CapabilitiesPath => $@"Software\{AppIdentity.CanonicalId}\Capabilities";

    public static void CleanupLegacyRegistrations(FileLogger logger)
    {
        try
        {
            using var registeredApps = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications", true);
            registeredApps.DeleteValue($"{AppIdentity.CanonicalId}.exe", false);
            registeredApps.DeleteValue(AppIdentity.LegacyCanonicalId, false);
            registeredApps.DeleteValue($"{AppIdentity.LegacyCanonicalId}.exe", false);

            DeleteKey(@"Software\" + AppIdentity.LegacyCanonicalId, logger);
            DeleteKey(@"Software\Classes\" + AppIdentity.LegacyProgId, logger);
            DeleteKey(@"Software\Classes\Applications\" + AppIdentity.LegacyCanonicalId + ".exe", logger);
            DeleteOpenWithValue("http", AppIdentity.LegacyProgId, logger);
            DeleteOpenWithValue("https", AppIdentity.LegacyProgId, logger);
        }
        catch (Exception ex)
        {
            logger.Error("Legacy registry cleanup failed.", ex);
        }
    }

    public static void EnsureRegistered(string exePath, FileLogger logger)
    {
        try
        {
            var appExeName = Path.GetFileName(exePath);
            var appCapabilitiesPath = $@"Software\Classes\Applications\{appExeName}\Capabilities";

            RegisterProgId(exePath);
            RegisterCapabilities(CapabilitiesPath);
            RegisterApplicationEntry(exePath, appExeName, appCapabilitiesPath);
            RegisterOpenWithProgIds();
            RegisterRegisteredApplications(CapabilitiesPath);
            NotifyAssociationsChanged();

            logger.Info("Protocol associations registered/refreshed.");
        }
        catch (Exception ex)
        {
            logger.Error("Failed to register protocol associations.", ex);
        }
    }

    private static void RegisterProgId(string exePath)
    {
        using var progIdKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{AppIdentity.ProgId}", true);
        progIdKey.SetValue(string.Empty, AppIdentity.DisplayName);
        progIdKey.SetValue("URL Protocol", string.Empty);

        using var defaultIconKey = progIdKey.CreateSubKey("DefaultIcon", true);
        defaultIconKey.SetValue(string.Empty, $"\"{exePath}\",0");

        using var applicationKey = progIdKey.CreateSubKey("Application", true);
        applicationKey.SetValue("ApplicationName", AppIdentity.DisplayName);
        applicationKey.SetValue("ApplicationDescription", "Captures and forwards HTTP/HTTPS links.");
        applicationKey.SetValue("ApplicationIcon", $"\"{exePath}\",0");

        using var commandKey = progIdKey.CreateSubKey(@"shell\open\command", true);
        commandKey.SetValue(string.Empty, $"\"{exePath}\" \"%1\"");
    }

    private static void RegisterCapabilities(string capabilitiesPath)
    {
        using var capabilitiesKey = Registry.CurrentUser.CreateSubKey(capabilitiesPath, true);
        capabilitiesKey.SetValue("ApplicationName", AppIdentity.DisplayName);
        capabilitiesKey.SetValue("ApplicationDescription", "Captures and forwards HTTP/HTTPS links.");

        using var urlAssociations = capabilitiesKey.CreateSubKey("URLAssociations", true);
        urlAssociations.SetValue("http", AppIdentity.ProgId);
        urlAssociations.SetValue("https", AppIdentity.ProgId);
    }

    private static void RegisterApplicationEntry(string exePath, string appExeName, string appCapabilitiesPath)
    {
        using var appKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\Applications\{appExeName}", true);
        appKey.SetValue("FriendlyAppName", AppIdentity.DisplayName);

        using var appCommandKey = appKey.CreateSubKey(@"shell\open\command", true);
        appCommandKey.SetValue(string.Empty, $"\"{exePath}\" \"%1\"");

        RegisterCapabilities(appCapabilitiesPath);
    }

    private static void RegisterOpenWithProgIds()
    {
        using var httpOpenWith = Registry.CurrentUser.CreateSubKey(@"Software\Classes\http\OpenWithProgids", true);
        httpOpenWith.SetValue(AppIdentity.ProgId, Array.Empty<byte>(), RegistryValueKind.None);

        using var httpsOpenWith = Registry.CurrentUser.CreateSubKey(@"Software\Classes\https\OpenWithProgids", true);
        httpsOpenWith.SetValue(AppIdentity.ProgId, Array.Empty<byte>(), RegistryValueKind.None);
    }

    private static void RegisterRegisteredApplications(string capabilitiesPath)
    {
        using var registeredApps = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications", true);
        registeredApps.SetValue(AppIdentity.CanonicalId, capabilitiesPath);
    }

    private static void DeleteOpenWithValue(string protocol, string progId, FileLogger logger)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{protocol}\OpenWithProgids", true);
            key.DeleteValue(progId, false);
        }
        catch (Exception ex)
        {
            logger.Error($"Failed to delete OpenWith entry for {protocol}:{progId}.", ex);
        }
    }

    private static void DeleteKey(string relativePath, FileLogger logger)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(relativePath, false);
        }
        catch (Exception ex)
        {
            logger.Error($"Failed to delete registry key HKCU\\{relativePath}.", ex);
        }
    }

    private static void NotifyAssociationsChanged()
    {
        SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
