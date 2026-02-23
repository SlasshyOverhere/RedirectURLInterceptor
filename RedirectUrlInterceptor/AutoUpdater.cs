using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace RedirectUrlInterceptor;

internal sealed class AutoUpdater
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Uri LatestReleaseApiUri =
        new($"https://api.github.com/repos/{AppIdentity.GitHubOwner}/{AppIdentity.GitHubRepository}/releases/latest");

    private readonly FileLogger _logger;

    public AutoUpdater(FileLogger logger)
    {
        _logger = logger;
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var latestRelease = await FetchLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            if (latestRelease is null)
            {
                return UpdateCheckResult.Fail("Could not load release metadata.");
            }

            var currentVersion = GetCurrentVersion();
            var updateAvailable = latestRelease.Version > currentVersion;
            if (!updateAvailable &&
                currentVersion == new Version(1, 0, 0, 0) &&
                latestRelease.Version.Major == 0)
            {
                // Older builds had default assembly version 1.0.0.0.
                // Allow migration to real tagged releases (v0.x.y).
                updateAvailable = true;
            }

            return UpdateCheckResult.Ok(currentVersion, latestRelease, updateAvailable);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error("Update check failed.", ex);
            return UpdateCheckResult.Fail(ex.Message);
        }
    }

    public async Task<LatestReleaseResult> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            var latestRelease = await FetchLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            if (latestRelease is null)
            {
                return LatestReleaseResult.Fail("No stable GitHub release is currently available.");
            }

            return LatestReleaseResult.Ok(latestRelease);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed loading latest release metadata.", ex);
            return LatestReleaseResult.Fail(ex.Message);
        }
    }

    public async Task<UpdateDownloadResult> DownloadUpdateAsync(
        UpdateReleaseInfo release,
        CancellationToken cancellationToken,
        IProgress<UpdateDownloadProgress>? progress = null)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.UpdatesDirectory);
            var tagToken = SanitizeFileToken(release.TagName);
            var targetFileName = $"{Path.GetFileNameWithoutExtension(AppIdentity.ReleaseAssetExeName)}-{tagToken}.exe";
            var destinationPath = Path.Combine(AppPaths.UpdatesDirectory, targetFileName);

            progress?.Report(UpdateDownloadProgress.StartingDownload(release.TagName));
            using var request = new HttpRequestMessage(HttpMethod.Get, release.AssetDownloadUrl);
            using var response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[128 * 1024];
            long downloadedBytes = 0;
            while (true)
            {
                var bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                downloadedBytes += bytesRead;
                progress?.Report(UpdateDownloadProgress.Downloading(downloadedBytes, totalBytes));
            }

            progress?.Report(UpdateDownloadProgress.Verifying());

            var checksumResult = await VerifyChecksumIfAvailableAsync(destinationPath, release, cancellationToken).ConfigureAwait(false);
            if (!checksumResult.Success)
            {
                TryDeleteFile(destinationPath);
                return UpdateDownloadResult.Fail(checksumResult.ErrorMessage ?? "Checksum verification failed.");
            }

            progress?.Report(UpdateDownloadProgress.Completed(destinationPath));
            return UpdateDownloadResult.Ok(destinationPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed downloading update '{release.TagName}'.", ex);
            return UpdateDownloadResult.Fail(ex.Message);
        }
    }

    public bool TryLaunchInPlaceUpdate(string downloadedExePath, out string? errorMessage)
    {
        errorMessage = null;

        try
        {
            if (string.IsNullOrWhiteSpace(downloadedExePath) || !File.Exists(downloadedExePath))
            {
                errorMessage = "Downloaded update file was not found.";
                return false;
            }

            var currentExePath = Application.ExecutablePath;
            if (string.IsNullOrWhiteSpace(currentExePath) || !File.Exists(currentExePath))
            {
                errorMessage = "Current executable path could not be resolved.";
                return false;
            }

            Directory.CreateDirectory(AppPaths.UpdatesDirectory);
            var scriptPath = Path.Combine(AppPaths.UpdatesDirectory, $"apply-update-{Guid.NewGuid():N}.cmd");
            File.WriteAllText(scriptPath, BuildUpdateScript(currentExePath, downloadedExePath, Environment.ProcessId), Encoding.ASCII);

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{scriptPath}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = AppPaths.UpdatesDirectory
            });

            if (process is null)
            {
                errorMessage = "Failed to launch updater process.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed launching update script.", ex);
            errorMessage = ex.Message;
            return false;
        }
    }

    private async Task<UpdateReleaseInfo?> FetchLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUri);
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var releaseDto = await JsonSerializer.DeserializeAsync<GitHubReleaseDto>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        if (releaseDto is null || releaseDto.Draft || releaseDto.Prerelease)
        {
            return null;
        }

        if (!TryParseVersion(releaseDto.TagName, out var releaseVersion))
        {
            throw new InvalidOperationException($"Release tag '{releaseDto.TagName}' is not a valid version.");
        }

        var asset = releaseDto.Assets?.FirstOrDefault(asset =>
            string.Equals(asset.Name, AppIdentity.ReleaseAssetExeName, StringComparison.OrdinalIgnoreCase))
            ?? releaseDto.Assets?.FirstOrDefault(asset =>
                asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

        var checksumAsset = releaseDto.Assets?.FirstOrDefault(asset =>
            string.Equals(asset.Name, $"{AppIdentity.ReleaseAssetExeName}.sha256", StringComparison.OrdinalIgnoreCase));

        if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
        {
            throw new InvalidOperationException("No downloadable EXE asset found in latest release.");
        }

        return new UpdateReleaseInfo(
            releaseDto.TagName,
            releaseVersion,
            asset.Name,
            asset.BrowserDownloadUrl,
            checksumAsset?.BrowserDownloadUrl,
            releaseDto.HtmlUrl ?? string.Empty);
    }

    private async Task<ChecksumVerificationResult> VerifyChecksumIfAvailableAsync(
        string downloadedFilePath,
        UpdateReleaseInfo release,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(release.ChecksumAssetDownloadUrl))
        {
            return ChecksumVerificationResult.Ok();
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, release.ChecksumAssetDownloadUrl);
            using var response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var checksumContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var expectedHash = ParseSha256(checksumContent);
            if (string.IsNullOrWhiteSpace(expectedHash))
            {
                return ChecksumVerificationResult.Fail("Release checksum file is invalid.");
            }

            await using var stream = new FileStream(downloadedFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();

            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                return ChecksumVerificationResult.Fail("Downloaded update failed checksum validation.");
            }

            return ChecksumVerificationResult.Ok();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error("Checksum validation failed.", ex);
            return ChecksumVerificationResult.Fail(ex.Message);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppIdentity.CanonicalId}/auto-updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static Version GetCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        if (TryParseVersion(assembly.GetName().Version?.ToString(), out var assemblyVersion))
        {
            return assemblyVersion;
        }

        if (TryParseVersion(Application.ProductVersion, out var productVersion))
        {
            return productVersion;
        }

        return new Version(0, 0, 0, 0);
    }

    private static bool TryParseVersion(string? value, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        var suffixIndex = normalized.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
        {
            normalized = normalized[..suffixIndex];
        }

        var segments = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length is < 2 or > 4)
        {
            return false;
        }

        var parsedSegments = new int[4];
        for (var i = 0; i < segments.Length; i++)
        {
            if (!int.TryParse(segments[i], out parsedSegments[i]))
            {
                return false;
            }
        }

        version = new Version(parsedSegments[0], parsedSegments[1], parsedSegments[2], parsedSegments[3]);
        return true;
    }

    private static string SanitizeFileToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            builder.Append(invalidChars.Contains(ch) ? '_' : ch);
        }

        return builder.ToString();
    }

    private static string BuildUpdateScript(string currentExePath, string downloadedExePath, int currentProcessId)
    {
        var target = EscapeForBatch(currentExePath);
        var source = EscapeForBatch(downloadedExePath);
        return $"""
@echo off
setlocal enableextensions
set "TARGET={target}"
set "SOURCE={source}"
set "PID={currentProcessId}"
set "SELF=%~f0"

for /l %%I in (1,1,90) do (
  tasklist /FI "PID eq %PID%" 2>nul | findstr /I "%PID%" >nul
  if errorlevel 1 goto wait_done
  timeout /t 1 /nobreak >nul
)

:wait_done
for /l %%I in (1,1,90) do (
  >nul 2>nul copy /y "%SOURCE%" "%TARGET%" && goto launch
  timeout /t 1 /nobreak >nul
)

:launch
start "" "%TARGET%"
goto cleanup

:cleanup
del /f /q "%SOURCE%" >nul 2>nul
del /f /q "%SELF%" >nul 2>nul
exit /b 0
""";
    }

    private static string EscapeForBatch(string path)
    {
        return path.Replace("%", "%%", StringComparison.Ordinal);
    }

    private static string? ParseSha256(string checksumFileContent)
    {
        if (string.IsNullOrWhiteSpace(checksumFileContent))
        {
            return null;
        }

        var token = checksumFileContent
            .Trim()
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(token) || token.Length != 64)
        {
            return null;
        }

        return token.All(ch => Uri.IsHexDigit(ch)) ? token.ToLowerInvariant() : null;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort cleanup only
        }
    }

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = string.Empty;

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }

        [JsonPropertyName("assets")]
        public List<GitHubAssetDto>? Assets { get; init; }
    }

    private sealed class GitHubAssetDto
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; init; } = string.Empty;
    }
}

internal sealed record UpdateReleaseInfo(
    string TagName,
    Version Version,
    string AssetName,
    string AssetDownloadUrl,
    string? ChecksumAssetDownloadUrl,
    string ReleasePageUrl);

internal sealed record UpdateCheckResult(
    bool Success,
    bool UpdateAvailable,
    Version CurrentVersion,
    UpdateReleaseInfo? Release,
    string? ErrorMessage)
{
    public static UpdateCheckResult Ok(Version currentVersion, UpdateReleaseInfo release, bool updateAvailable)
    {
        return new UpdateCheckResult(true, updateAvailable, currentVersion, release, null);
    }

    public static UpdateCheckResult Fail(string errorMessage)
    {
        return new UpdateCheckResult(false, false, new Version(0, 0, 0, 0), null, errorMessage);
    }
}

internal sealed record UpdateDownloadResult(bool Success, string? DownloadedExePath, string? ErrorMessage)
{
    public static UpdateDownloadResult Ok(string downloadedExePath)
    {
        return new UpdateDownloadResult(true, downloadedExePath, null);
    }

    public static UpdateDownloadResult Fail(string errorMessage)
    {
        return new UpdateDownloadResult(false, null, errorMessage);
    }
}

internal sealed record UpdateDownloadProgress(
    string StatusText,
    long DownloadedBytes,
    long? TotalBytes,
    bool IsIndeterminate)
{
    public static UpdateDownloadProgress StartingDownload(string tagName)
    {
        return new UpdateDownloadProgress($"Downloading {tagName}...", 0, null, true);
    }

    public static UpdateDownloadProgress Downloading(long downloadedBytes, long? totalBytes)
    {
        return new UpdateDownloadProgress("Downloading update...", downloadedBytes, totalBytes, totalBytes is null || totalBytes <= 0);
    }

    public static UpdateDownloadProgress Verifying()
    {
        return new UpdateDownloadProgress("Verifying package...", 0, null, true);
    }

    public static UpdateDownloadProgress Completed(string destinationPath)
    {
        return new UpdateDownloadProgress($"Downloaded to {destinationPath}", 0, null, true);
    }
}

internal sealed record LatestReleaseResult(bool Success, UpdateReleaseInfo? Release, string? ErrorMessage)
{
    public static LatestReleaseResult Ok(UpdateReleaseInfo release)
    {
        return new LatestReleaseResult(true, release, null);
    }

    public static LatestReleaseResult Fail(string errorMessage)
    {
        return new LatestReleaseResult(false, null, errorMessage);
    }
}

internal sealed record ChecksumVerificationResult(bool Success, string? ErrorMessage)
{
    public static ChecksumVerificationResult Ok()
    {
        return new ChecksumVerificationResult(true, null);
    }

    public static ChecksumVerificationResult Fail(string errorMessage)
    {
        return new ChecksumVerificationResult(false, errorMessage);
    }
}
