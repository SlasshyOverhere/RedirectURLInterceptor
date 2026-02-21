namespace RedirectUrlInterceptor;

internal static class ProcessNameHelper
{
    public static string Normalize(string? processNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(processNameOrPath))
        {
            return string.Empty;
        }

        var filename = Path.GetFileName(processNameOrPath.Trim());
        var noExtension = Path.GetFileNameWithoutExtension(filename);
        return noExtension.Trim().ToLowerInvariant();
    }

    public static List<string> NormalizeMany(IEnumerable<string>? processNames)
    {
        if (processNames is null)
        {
            return [];
        }

        return processNames
            .Select(Normalize)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string ToExeName(string? processName)
    {
        var normalized = Normalize(processName);
        return string.IsNullOrWhiteSpace(normalized) ? string.Empty : $"{normalized}.exe";
    }
}
