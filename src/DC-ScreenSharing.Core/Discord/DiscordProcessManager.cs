using System.Diagnostics;
using DCScreenSharing.Shared.Logging;

namespace DCScreenSharing.Core.Discord;

public class DiscordProcessManager
{
    private readonly IAppLogger _logger;

    public DiscordProcessManager(IAppLogger logger)
    {
        _logger = logger;
    }

    public async Task<bool> CloseDiscordGracefullyAsync(DiscordInstallation installation, int timeoutMs = 3500)
    {
        var targetProcesses = GetDiscordProcesses(installation);
        if (targetProcesses.Count == 0)
        {
            _logger.Info($"No active {installation.Flavor} processes found to close.");
            return true;
        }

        _logger.Info($"Attempting graceful close of {targetProcesses.Count} {installation.Flavor} processes...");

        // Request graceful exit by sending WM_CLOSE to main windows
        foreach (var proc in targetProcesses)
        {
            try
            {
                if (proc.MainWindowHandle != IntPtr.Zero)
                {
                    proc.CloseMainWindow();
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to send close message to PID {proc.Id}", ex);
            }
        }

        // Wait up to timeoutMs for processes to exit gracefully
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            targetProcesses = GetDiscordProcesses(installation);
            if (targetProcesses.Count == 0)
            {
                _logger.Info($"All {installation.Flavor} processes closed gracefully.");
                return true;
            }
            await Task.Delay(200);
        }

        // Force termination of any stubborn Discord processes belonging to this installation
        _logger.Warning($"Timeout reached. Forcing termination of {targetProcesses.Count} remaining {installation.Flavor} processes.");
        foreach (var proc in targetProcesses)
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Could not kill PID {proc.Id}", ex);
            }
        }

        await Task.Delay(300);
        var remaining = GetDiscordProcesses(installation);
        return remaining.Count == 0;
    }

    public Process? LaunchDiscord(DiscordInstallation installation)
    {
        try
        {
            ProcessStartInfo startInfo;
            if (!string.IsNullOrEmpty(installation.UpdateExePath) && File.Exists(installation.UpdateExePath))
            {
                var exeName = Path.GetFileName(installation.ExecutablePath);
                _logger.Info($"Launching Discord via Squirrel: {installation.UpdateExePath} --processStart {exeName}");
                startInfo = new ProcessStartInfo
                {
                    FileName = installation.UpdateExePath,
                    Arguments = $"--processStart {exeName}",
                    WorkingDirectory = installation.InstallationDirectory,
                    UseShellExecute = true
                };
            }
            else
            {
                _logger.Info($"Launching Discord directly: {installation.ExecutablePath}");
                startInfo = new ProcessStartInfo
                {
                    FileName = installation.ExecutablePath,
                    WorkingDirectory = Path.GetDirectoryName(installation.ExecutablePath) ?? installation.InstallationDirectory,
                    UseShellExecute = true
                };
            }

            var proc = Process.Start(startInfo);
            _logger.Info($"Discord launcher started with PID: {proc?.Id}");
            return proc;
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to launch Discord ({installation.Flavor})", ex);
            return null;
        }
    }

    public List<Process> GetDiscordProcesses(DiscordInstallation? installation = null)
    {
        var result = new List<Process>();
        var processNames = new[] { "Discord", "DiscordPTB", "DiscordCanary", "DiscordDevelopment" };

        foreach (var name in processNames)
        {
            var procs = Process.GetProcessesByName(name);
            foreach (var p in procs)
            {
                try
                {
                    if (installation != null && !string.IsNullOrEmpty(installation.ExecutablePath))
                    {
                        var path = p.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path) &&
                            (string.Equals(path, installation.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                             path.StartsWith(installation.InstallationDirectory, StringComparison.OrdinalIgnoreCase)))
                        {
                            result.Add(p);
                            continue;
                        }
                    }
                    else
                    {
                        result.Add(p);
                        continue;
                    }
                }
                catch
                {
                    // If MainModule is inaccessible (e.g. child helper), include it if name matches flavor
                    if (installation == null || name.Contains(installation.Flavor.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(p);
                    }
                }
            }
        }

        return result;
    }
}
