using System.Text.Json;

namespace RedirectUrlInterceptor;

internal sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static readonly string[] DefaultBrowserProcesses =
    [
        "chrome",
        "msedge",
        "firefox",
        "brave",
        "opera",
        "iexplore"
    ];

    public bool InterceptionEnabled { get; set; } = true;

    public bool ResolveRedirects { get; set; }

    public int RedirectMaxHops { get; set; } = 8;

    public int RedirectTimeoutSeconds { get; set; } = 6;

    public List<string> ExcludedParentProcesses { get; set; } = [];

    public List<string> BrowserProcesses { get; set; } = DefaultBrowserProcesses.ToList();

    public string? ForwardBrowserPath { get; set; }

    public bool ForwardInterceptedLinksToBrowser { get; set; } = true;

    public bool AutoUpdateEnabled { get; set; } = true;

    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    public static AppSettings Load(string path, FileLogger logger)
    {
        try
        {
            if (!File.Exists(path))
            {
                var settings = new AppSettings();
                settings.Normalize();
                settings.Save(path, logger);
                return settings;
            }

            var json = File.ReadAllText(path);
            var settingsFromFile = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            settingsFromFile.Normalize();
            return settingsFromFile;
        }
        catch (Exception ex)
        {
            logger.Error("Failed to load config. Reverting to defaults.", ex);
            var fallback = new AppSettings();
            fallback.Normalize();
            return fallback;
        }
    }

    public void Save(string path, FileLogger logger)
    {
        try
        {
            Normalize();
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            logger.Error("Failed to save config.", ex);
        }
    }

    public void Normalize()
    {
        RedirectMaxHops = Math.Clamp(RedirectMaxHops, 1, 50);
        RedirectTimeoutSeconds = Math.Clamp(RedirectTimeoutSeconds, 1, 60);

        ExcludedParentProcesses = ProcessNameHelper.NormalizeMany(ExcludedParentProcesses);

        BrowserProcesses = ProcessNameHelper.NormalizeMany(BrowserProcesses);
        if (BrowserProcesses.Count == 0)
        {
            BrowserProcesses = DefaultBrowserProcesses.ToList();
        }

        if (!string.IsNullOrWhiteSpace(ForwardBrowserPath))
        {
            ForwardBrowserPath = ForwardBrowserPath.Trim();
        }

        if (LastUpdateCheckUtc is { } checkedAt)
        {
            if (checkedAt < DateTimeOffset.UnixEpoch || checkedAt > DateTimeOffset.UtcNow.AddMinutes(5))
            {
                LastUpdateCheckUtc = null;
            }
        }
    }
}
