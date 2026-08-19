using System.IO;
using System.IO.Compression;
using System.Text.Json;
using DCScreenSharing.Core.Discord;
using DCScreenSharing.Networking;
using DCScreenSharing.Shared.Logging;

namespace DCScreenSharing.App.Services;

public class DiagnosticsService
{
    private readonly IAppLogger _logger;
    private readonly NetworkServiceClient _networkClient;
    private readonly DiscordLocator _discordLocator;

    public DiagnosticsService(IAppLogger logger, NetworkServiceClient networkClient, DiscordLocator discordLocator)
    {
        _logger = logger;
        _networkClient = networkClient;
        _discordLocator = discordLocator;
    }

    public async Task<string?> ExportDiagnosticsZipAsync(string targetZipPath)
    {
        try
        {
            _logger.Info($"Generating diagnostics bundle at: {targetZipPath}");
            var tempDir = Path.Combine(Path.GetTempPath(), "DCSS_Diagnostics_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            // 1. Collect System & Discord info
            var discordInstalls = _discordLocator.DiscoverAllInstallations();
            var serviceDiagnostics = await _networkClient.GetDiagnosticsAsync();

            var systemInfo = new
            {
                appVersion = "1.0.0",
                timestampUtc = DateTime.UtcNow,
                osVersion = Environment.OSVersion.VersionString,
                is64BitOS = Environment.Is64BitOperatingSystem,
                is64BitProcess = Environment.Is64BitProcess,
                dotNetVersion = Environment.Version.ToString(),
                discordInstallations = discordInstalls.Select(d => new
                {
                    flavor = d.Flavor.ToString(),
                    version = d.VersionString,
                    executablePath = d.ExecutablePath,
                    isRunning = d.IsRunning,
                    runningPids = d.RunningProcessIds
                }),
                serviceStatus = serviceDiagnostics
            };

            var sysInfoJson = JsonSerializer.Serialize(systemInfo, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(tempDir, "system_info.json"), sysInfoJson);

            // 2. Copy and sanitize log files
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

            CopyAndSanitizeLog(Path.Combine(localAppData, "DC-ScreenSharing", "logs", "app.log"), Path.Combine(tempDir, "app.log"));
            CopyAndSanitizeLog(Path.Combine(commonAppData, "DC-ScreenSharing", "logs", "service.log"), Path.Combine(tempDir, "service.log"));
            CopyAndSanitizeLog(Path.Combine(localAppData, "DC-ScreenSharing", "logs", "updater.log"), Path.Combine(tempDir, "updater.log"));

            if (File.Exists(targetZipPath))
            {
                File.Delete(targetZipPath);
            }

            ZipFile.CreateFromDirectory(tempDir, targetZipPath);
            Directory.Delete(tempDir, recursive: true);

            _logger.Info("Diagnostics bundle successfully created.");
            return targetZipPath;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to create diagnostics bundle", ex);
            return null;
        }
    }

    private static void CopyAndSanitizeLog(string sourcePath, string destPath)
    {
        try
        {
            if (File.Exists(sourcePath))
            {
                var lines = File.ReadAllLines(sourcePath);
                var sanitizedLines = lines.Select(Sanitizer.Sanitize);
                File.WriteAllLines(destPath, sanitizedLines);
            }
        }
        catch { }
    }
}
