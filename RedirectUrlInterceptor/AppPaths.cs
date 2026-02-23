namespace RedirectUrlInterceptor;

internal static class AppPaths
{
    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppIdentity.DataFolderName);

    public static string LegacyDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppIdentity.LegacyDataFolderName);

    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public static string UpdatesDirectory => Path.Combine(DataDirectory, "updates");

    public static string ConfigPath => Path.Combine(DataDirectory, "config.json");

    public static string AppLogPath => Path.Combine(DataDirectory, "app.log");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(UpdatesDirectory);
    }

    public static void CleanupLegacyData(FileLogger logger)
    {
        try
        {
            if (!Directory.Exists(LegacyDataDirectory))
            {
                return;
            }

            Directory.Delete(LegacyDataDirectory, true);
            logger.Info($"Removed legacy data folder: {LegacyDataDirectory}");
        }
        catch (Exception ex)
        {
            logger.Error("Failed to remove legacy data folder.", ex);
        }
    }
}
