namespace RedirectUrlInterceptor;

internal static class BrowserLocator
{
    private static readonly string[] CandidatePaths =
    [
        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Mozilla Firefox\firefox.exe",
        @"C:\Program Files (x86)\Mozilla Firefox\firefox.exe",
        @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe",
        @"C:\Program Files (x86)\BraveSoftware\Brave-Browser\Application\brave.exe",
        @"C:\Users\%USERNAME%\AppData\Local\Programs\Opera\opera.exe"
    ];

    public static string? TryFindFirstInstalled()
    {
        foreach (var candidate in CandidatePaths)
        {
            var expanded = Environment.ExpandEnvironmentVariables(candidate);
            if (File.Exists(expanded))
            {
                return expanded;
            }
        }

        return null;
    }
}
