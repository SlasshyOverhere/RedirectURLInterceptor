using Microsoft.Win32;

namespace RedirectUrlInterceptor;

internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = AppIdentity.CanonicalId;
    private const string LegacyValueName = AppIdentity.LegacyCanonicalId;

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            var primary = key?.GetValue(ValueName) as string;
            if (!string.IsNullOrWhiteSpace(primary))
            {
                return true;
            }

            var legacy = key?.GetValue(LegacyValueName) as string;
            return !string.IsNullOrWhiteSpace(legacy);
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled, string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{exePath}\"");
            key.DeleteValue(LegacyValueName, false);
        }
        else
        {
            key.DeleteValue(ValueName, false);
            key.DeleteValue(LegacyValueName, false);
        }
    }
}
