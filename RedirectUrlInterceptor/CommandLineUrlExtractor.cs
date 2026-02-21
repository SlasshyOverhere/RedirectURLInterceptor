using System.Text.RegularExpressions;

namespace RedirectUrlInterceptor;

internal static partial class CommandLineUrlExtractor
{
    [GeneratedRegex(@"(?<url>[a-zA-Z][a-zA-Z0-9+\-.]*:[^\s\""'<>]+)", RegexOptions.Compiled)]
    private static partial Regex UrlRegex();

    public static IEnumerable<string> Extract(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            yield break;
        }

        var uniqueUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in UrlRegex().Matches(commandLine))
        {
            var raw = match.Groups["url"].Value.Trim().TrimEnd('.', ',', ';', ')', ']');
            if (!LooksLikeUrl(raw))
            {
                continue;
            }

            if (!Uri.TryCreate(raw, UriKind.Absolute, out _))
            {
                continue;
            }

            if (uniqueUrls.Add(raw))
            {
                yield return raw;
            }
        }
    }

    private static bool LooksLikeUrl(string value)
    {
        if (value.Length < 4)
        {
            return false;
        }

        // Ignore drive paths like C:\Program Files\...
        if (char.IsAsciiLetter(value[0]) && value.Length > 2 && value[1] == ':' && (value[2] == '\\' || value[2] == '/'))
        {
            return false;
        }

        if (value.Contains("://", StringComparison.Ordinal))
        {
            return true;
        }

        return value.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
    }
}
