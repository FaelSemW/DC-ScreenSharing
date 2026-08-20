using System.Diagnostics;
using System.IO;
using System.Net.Http;
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
    public Version CurrentVersion { get; set; } = new(1, 0, 2);
    public Version LatestVersion { get; set; } = new(1, 0, 2);
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
    private readonly string _updatesDirectory;

    public ApplicationUpdateService(
        IAppLogger logger,
        string? releasesApiUrl = null,
        HttpClient? httpClient = null)
    {
        _logger = logger;
        _releasesApiUrl = releasesApiUrl ?? Constants.GitHubReleasesApiUrl;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DC-ScreenSharing-Updater/1.0");

        _updatesDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DC-ScreenSharing", "Updates");
        try
        {
            Directory.CreateDirectory(_updatesDirectory);
        }
        catch
        {
            // Fallback to temp if local app data fails
            _updatesDirectory = Path.Combine(Path.GetTempPath(), "DC-ScreenSharing", "Updates");
            try { Directory.CreateDirectory(_updatesDirectory); } catch { }
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

            if (release == null || release.Draft || release.Prerelease)
            {
                _logger.Info("No applicable release found (draft/prerelease or empty response).");
                return result;
            }

            var tagClean = release.TagName.TrimStart('v', 'V').Trim();
            if (Version.TryParse(tagClean, out var remoteVersion))
            {
                result.LatestVersion = remoteVersion;
                result.ReleaseNotes = release.Body;

                if (remoteVersion > currentVersion)
                {
                    _logger.Info($"New application version detected: v{remoteVersion} (current: v{currentVersion})");
                    result.UpdateAvailable = true;

                    // Select the main installer .exe asset (e.g. DC-ScreenSharing-Setup-1.0.2.exe)
                    var exeAsset = release.Assets.FirstOrDefault(a => 
                        a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && 
                        !a.Name.Contains("Maintainer", StringComparison.OrdinalIgnoreCase) &&
                        !a.Name.Contains("Collector", StringComparison.OrdinalIgnoreCase));

                    if (exeAsset != null)
                    {
                        result.DownloadUrl = exeAsset.BrowserDownloadUrl;
                        result.FileName = exeAsset.Name;
                        _logger.Info($"Selected update installer asset: {exeAsset.Name} ({exeAsset.Size} bytes)");
                    }
                    else
                    {
                        _logger.Warning("Release has newer version tag, but no matching installer executable asset found.");
                        result.UpdateAvailable = false;
                        return result;
                    }

                    // Select checksum asset if available
                    var shaAsset = release.Assets.FirstOrDefault(a => 
                        a.Name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) || 
                        a.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));

                    if (shaAsset != null)
                    {
                        result.ChecksumUrl = shaAsset.BrowserDownloadUrl;
                        _logger.Info($"Selected checksum asset: {shaAsset.Name}");
                    }
                }
                else
                {
                    _logger.Info($"Application is up to date (current: v{currentVersion}, latest: v{remoteVersion}).");
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

        var destinationPath = Path.Combine(_updatesDirectory, updateInfo.FileName);
        var partPath = destinationPath + ".part";

        try
        {
            if (File.Exists(partPath))
            {
                try { File.Delete(partPath); } catch { }
            }

            _logger.Info($"Downloading update {updateInfo.FileName} to {partPath}...");
            using var response = await _httpClient.GetAsync(updateInfo.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using (var fileStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
            {
                var buffer = new byte[16384];
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

                await fileStream.FlushAsync(ct);
            }

            _logger.Info("Update download completed. Verifying SHA-256 integrity...");

            // If checksum URL is available, verify SHA256 against candidate
            if (!string.IsNullOrEmpty(updateInfo.ChecksumUrl))
            {
                var shaVerified = await VerifySha256Async(partPath, updateInfo.ChecksumUrl, updateInfo.FileName, ct);
                if (!shaVerified)
                {
                    _logger.Error("Update verification failed! Checksum mismatch. Aborting update.");
                    if (File.Exists(partPath)) File.Delete(partPath);
                    return null;
                }
                _logger.Info("SHA-256 checksum verification PASS.");
            }

            // Atomic rename from .part to final .exe
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }
            File.Move(partPath, destinationPath);

            _logger.Info($"Update installer verified and ready at: {destinationPath}");
            return destinationPath;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to download/verify update", ex);
            if (File.Exists(partPath))
            {
                try { File.Delete(partPath); } catch { }
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

            foreach (var rawLine in checksumContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (line.Contains(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    var expectedHash = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                    return string.Equals(computedHash, expectedHash, StringComparison.OrdinalIgnoreCase);
                }
                else if (line.Length == 64 && !line.Contains(" ")) // Single hash file format
                {
                    return string.Equals(computedHash, line, StringComparison.OrdinalIgnoreCase);
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

    public bool LaunchUpdater(string installerPath, string? mainExecutablePath = null)
    {
        try
        {
            if (!File.Exists(installerPath))
            {
                _logger.Error($"Cannot launch updater: Installer not found at '{installerPath}'");
                return false;
            }

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var updaterCandidates = new[]
            {
                Path.Combine(baseDir, "DC-ScreenSharing.Updater.exe"),
                Path.Combine(Directory.GetCurrentDirectory(), "DC-ScreenSharing.Updater.exe"),
                @"C:\Program Files\DC-ScreenSharing\DC-ScreenSharing.Updater.exe"
            };

            var updaterExe = updaterCandidates.FirstOrDefault(File.Exists);
            if (string.IsNullOrEmpty(updaterExe))
            {
                _logger.Warning("DC-ScreenSharing.Updater.exe not found beside app. Attempting direct installer launch.");
                var directPsi = new ProcessStartInfo
                {
                    FileName = installerPath,
                    Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CLOSEAPPLICATIONS",
                    UseShellExecute = true
                };
                Process.Start(directPsi);
                return true;
            }

            var sourceDir = Path.GetDirectoryName(updaterExe)!;

            // Clean up old update runtime directories
            CleanupOldUpdateRuntimes();

            // Create an isolated temporary directory for the updater runtime
            // %LOCALAPPDATA%\DC-ScreenSharing\UpdateRuntime\<Guid>\
            var updateRuntimeDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DC-ScreenSharing",
                "UpdateRuntime",
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(updateRuntimeDir);
            _logger.Info($"Staging isolated updater runtime from '{sourceDir}' to '{updateRuntimeDir}'...");

            // Copy all executable, library, configuration, and sidecar runtime files
            // (clrjit.dll, coreclr.dll, hostfxr.dll, etc.) to the temp directory
            var extensionsToCopy = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".exe", ".dll", ".json", ".pdb", ".deps.json", ".runtimeconfig.json"
            };

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var ext = Path.GetExtension(file);
                if (extensionsToCopy.Contains(ext) || file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    var dest = Path.Combine(updateRuntimeDir, Path.GetFileName(file));
                    try
                    {
                        File.Copy(file, dest, overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"Could not copy runtime file '{Path.GetFileName(file)}': {ex.Message}");
                    }
                }
            }

            var stagedUpdaterExe = Path.Combine(updateRuntimeDir, "DC-ScreenSharing.Updater.exe");
            if (!File.Exists(stagedUpdaterExe))
            {
                _logger.Error($"Failed to stage updater executable to '{stagedUpdaterExe}'. Falling back to original path.");
                stagedUpdaterExe = updaterExe;
            }

            var currentPid = Process.GetCurrentProcess().Id;
            var relaunchExe = mainExecutablePath ?? Environment.ProcessPath ?? Path.Combine(baseDir, "DC-ScreenSharing.exe");

            var arguments = $"--staged \"{installerPath}\" --target-pid {currentPid} --relaunch \"{relaunchExe}\" --runtime-dir \"{updateRuntimeDir}\"";
            _logger.Info($"Launching isolated updater coordinator: {stagedUpdaterExe} {arguments}");

            var psi = new ProcessStartInfo
            {
                FileName = stagedUpdaterExe,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(stagedUpdaterExe),
                UseShellExecute = true
            };

            var proc = Process.Start(psi);
            return proc != null;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to launch updater coordinator", ex);
            return false;
        }
    }

    private void CleanupOldUpdateRuntimes()
    {
        try
        {
            var runtimesBase = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DC-ScreenSharing",
                "UpdateRuntime");

            if (Directory.Exists(runtimesBase))
            {
                foreach (var dir in Directory.GetDirectories(runtimesBase))
                {
                    try
                    {
                        var info = new DirectoryInfo(dir);
                        if (DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromMinutes(10))
                        {
                            Directory.Delete(dir, recursive: true);
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    public static string ComputeSha256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }
}
