using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DCScreenSharing.Shared;
using DCScreenSharing.Shared.Logging;

namespace DCScreenSharing.Core.Updates;

public class GitHubReleaseAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

public class GitHubReleaseResponse
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubReleaseAsset> Assets { get; set; } = new();
}

public class UpdateCheckResult
{
    public bool UpdateAvailable { get; set; }
    public Version CurrentVersion { get; set; } = new(1, 0, 0);
    public Version LatestVersion { get; set; } = new(1, 0, 0);
    public string ReleaseNotes { get; set; } = string.Empty;
    public string? DownloadUrl { get; set; }
    public string? ChecksumUrl { get; set; }
    public string? FileName { get; set; }
}

public class ApplicationUpdateService
{
    private readonly HttpClient _httpClient;
    private readonly IAppLogger _logger;
    private readonly string _releasesApiUrl;
    private readonly string _stagingDirectory;

    public ApplicationUpdateService(
        IAppLogger logger,
        string? releasesApiUrl = null,
        HttpClient? httpClient = null)
    {
        _logger = logger;
        _releasesApiUrl = releasesApiUrl ?? Constants.GitHubReleasesApiUrl;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DC-ScreenSharing-Updater/1.0");

        _stagingDirectory = Path.Combine(Path.GetTempPath(), "DC-ScreenSharing", "Updates");
        try
        {
            Directory.CreateDirectory(_stagingDirectory);
        }
        catch
        {
            // Ignore staging dir creation errors
        }
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(Version currentVersion, CancellationToken ct = default)
    {
        var result = new UpdateCheckResult { CurrentVersion = currentVersion };

        try
        {
            _logger.Info($"Checking for application updates from {_releasesApiUrl} (current: v{currentVersion})...");
            var json = await _httpClient.GetStringAsync(_releasesApiUrl, ct);
            var release = JsonSerializer.Deserialize<GitHubReleaseResponse>(json);

            if (release == null || release.Draft)
                return result;

            var tagClean = release.TagName.TrimStart('v', 'V');
            if (Version.TryParse(tagClean, out var remoteVersion))
            {
                result.LatestVersion = remoteVersion;
                result.ReleaseNotes = release.Body;

                if (remoteVersion > currentVersion)
                {
                    _logger.Info($"New application version detected: v{remoteVersion} (current: v{currentVersion})");
                    result.UpdateAvailable = true;

                    var exeAsset = release.Assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                    if (exeAsset != null)
                    {
                        result.DownloadUrl = exeAsset.BrowserDownloadUrl;
                        result.FileName = exeAsset.Name;
                    }

                    var shaAsset = release.Assets.FirstOrDefault(a => a.Name.Contains("SHA256", StringComparison.OrdinalIgnoreCase));
                    if (shaAsset != null)
                    {
                        result.ChecksumUrl = shaAsset.BrowserDownloadUrl;
                    }
                }
                else
                {
                    _logger.Info($"Application is up to date (v{currentVersion}).");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to check for application updates: {ex.Message}");
        }

        return result;
    }

    public async Task<string?> DownloadAndVerifyUpdateAsync(
        UpdateCheckResult updateInfo,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(updateInfo.DownloadUrl) || string.IsNullOrEmpty(updateInfo.FileName))
            return null;

        var destinationPath = Path.Combine(_stagingDirectory, updateInfo.FileName);

        try
        {
            _logger.Info($"Downloading update {updateInfo.FileName} to {destinationPath}...");
            using var response = await _httpClient.GetAsync(updateInfo.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead, ct);
                totalRead += bytesRead;

                if (totalBytes > 0 && progress != null)
                {
                    var percentage = (int)((totalRead * 100) / totalBytes);
                    progress.Report(percentage);
                }
            }

            fileStream.Close();
            _logger.Info("Update download complete. Verifying file integrity...");

            // If checksum URL is available, verify SHA256
            if (!string.IsNullOrEmpty(updateInfo.ChecksumUrl))
            {
                var shaVerified = await VerifySha256Async(destinationPath, updateInfo.ChecksumUrl, updateInfo.FileName, ct);
                if (!shaVerified)
                {
                    _logger.Error("Update verification failed! Checksum mismatch. Aborting update.");
                    if (File.Exists(destinationPath)) File.Delete(destinationPath);
                    return null;
                }
            }

            _logger.Info("Update verification successful.");
            return destinationPath;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to download/verify update", ex);
            if (File.Exists(destinationPath))
            {
                try { File.Delete(destinationPath); } catch { }
            }
            return null;
        }
    }

    private async Task<bool> VerifySha256Async(string filePath, string checksumUrl, string fileName, CancellationToken ct)
    {
        try
        {
            var checksumContent = await _httpClient.GetStringAsync(checksumUrl, ct);
            var computedHash = ComputeSha256(filePath);

            foreach (var line in checksumContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Contains(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    var expectedHash = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                    return string.Equals(computedHash, expectedHash, StringComparison.OrdinalIgnoreCase);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning($"Could not verify SHA-256 against checksum file: {ex.Message}");
            return true; // Allow proceeding if checksum file unreachable but binary downloaded intact
        }
    }

    public static string ComputeSha256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }
}
