using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DCScreenSharing.Core.Discord;

public class DiscordLocator
{
    private static readonly Regex AppFolderRegex = new(@"^app-(\d+\.\d+\.\d+(?:\.\d+)?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IReadOnlyList<DiscordInstallation> DiscoverAllInstallations()
    {
        var installations = new List<DiscordInstallation>();

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData))
            return installations;

        var candidates = new (DiscordFlavor Flavor, string DirName, string ExeName)[]
        {
            (DiscordFlavor.Stable, "Discord", "Discord.exe"),
            (DiscordFlavor.PTB, "DiscordPTB", "DiscordPTB.exe"),
            (DiscordFlavor.Canary, "DiscordCanary", "DiscordCanary.exe"),
            (DiscordFlavor.Development, "DiscordDevelopment", "DiscordDevelopment.exe")
        };

        foreach (var (flavor, dirName, exeName) in candidates)
        {
            var baseDir = Path.Combine(localAppData, dirName);
            if (Directory.Exists(baseDir))
            {
                var inst = ProbeInstallationDirectory(flavor, baseDir, exeName);
                if (inst != null)
                {
                    installations.Add(inst);
                }
            }
        }

        // Also check running processes to find any installation in a non-standard path
        ProbeRunningProcesses(installations);

        // Update running state for all discovered installations
        UpdateRunningStatus(installations);

        return installations;
    }

    public DiscordInstallation? ResolveInstallation(DiscordFlavor preferredFlavor = DiscordFlavor.Automatic)
    {
        var all = DiscoverAllInstallations();
        if (all.Count == 0)
            return null;

        if (preferredFlavor != DiscordFlavor.Automatic)
        {
            var match = all.FirstOrDefault(i => i.Flavor == preferredFlavor);
            if (match != null)
                return match;
        }

        // If automatic, prefer currently running instance
        var running = all.FirstOrDefault(i => i.IsRunning);
        if (running != null)
            return running;

        // Otherwise prefer Stable > PTB > Canary > Development
        return all.OrderBy(i => i.Flavor switch
        {
            DiscordFlavor.Stable => 1,
            DiscordFlavor.PTB => 2,
            DiscordFlavor.Canary => 3,
            DiscordFlavor.Development => 4,
            _ => 5
        }).FirstOrDefault();
    }

    private DiscordInstallation? ProbeInstallationDirectory(DiscordFlavor flavor, string baseDir, string exeName)
    {
        try
        {
            var subDirs = Directory.GetDirectories(baseDir);
            var appDirs = new List<(Version Version, string Path)>();

            foreach (var dir in subDirs)
            {
                var dirName = Path.GetFileName(dir);
                var match = AppFolderRegex.Match(dirName);
                if (match.Success && Version.TryParse(match.Groups[1].Value, out var ver))
                {
                    var exePath = Path.Combine(dir, exeName);
                    // On some installs the exe might just be Discord.exe
                    if (!File.Exists(exePath))
                    {
                        exePath = Path.Combine(dir, "Discord.exe");
                    }

                    if (File.Exists(exePath))
                    {
                        appDirs.Add((ver, exePath));
                    }
                }
            }

            if (appDirs.Count == 0)
                return null;

            // Pick the highest version
            var latest = appDirs.OrderByDescending(x => x.Version).First();
            var updateExe = Path.Combine(baseDir, "Update.exe");

            return new DiscordInstallation
            {
                Flavor = flavor,
                InstallationDirectory = baseDir,
                ExecutablePath = latest.Path,
                UpdateExePath = File.Exists(updateExe) ? updateExe : null,
                Version = latest.Version
            };
        }
        catch
        {
            return null;
        }
    }

    private void ProbeRunningProcesses(List<DiscordInstallation> installations)
    {
        try
        {
            var processNames = new[] { "Discord", "DiscordPTB", "DiscordCanary", "DiscordDevelopment" };
            foreach (var name in processNames)
            {
                var procs = Process.GetProcessesByName(name);
                foreach (var proc in procs)
                {
                    try
                    {
                        var exePath = proc.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                        {
                            if (!installations.Any(i => string.Equals(i.ExecutablePath, exePath, StringComparison.OrdinalIgnoreCase)))
                            {
                                var dir = Path.GetDirectoryName(exePath) ?? string.Empty;
                                var baseDir = Directory.GetParent(dir)?.FullName ?? dir;
                                var flavor = name switch
                                {
                                    "DiscordPTB" => DiscordFlavor.PTB,
                                    "DiscordCanary" => DiscordFlavor.Canary,
                                    "DiscordDevelopment" => DiscordFlavor.Development,
                                    _ => DiscordFlavor.Stable
                                };

                                var versionInfo = FileVersionInfo.GetVersionInfo(exePath);
                                Version.TryParse(versionInfo.ProductVersion ?? "1.0.0", out var ver);

                                installations.Add(new DiscordInstallation
                                {
                                    Flavor = flavor,
                                    InstallationDirectory = baseDir,
                                    ExecutablePath = exePath,
                                    Version = ver ?? new Version(1, 0, 0),
                                    IsRunning = true,
                                    RunningProcessIds = new List<int> { proc.Id }
                                });
                            }
                        }
                    }
                    catch
                    {
                        // Access denied or process exited
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
        }
        catch
        {
            // Ignore process query errors
        }
    }

    private void UpdateRunningStatus(List<DiscordInstallation> installations)
    {
        var processNames = new[] { "Discord", "DiscordPTB", "DiscordCanary", "DiscordDevelopment" };
        var runningProcs = new List<(int Id, string ExePath)>();

        foreach (var name in processNames)
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try
                {
                    var path = proc.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path))
                    {
                        runningProcs.Add((proc.Id, path));
                    }
                }
                catch
                {
                    // Fallback to name matching
                    runningProcs.Add((proc.Id, name));
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }

        foreach (var inst in installations)
        {
            var matchedPids = runningProcs
                .Where(p => string.Equals(p.ExePath, inst.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                            p.ExePath.Contains(inst.Flavor.ToString(), StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Id)
                .Distinct()
                .ToList();

            inst.IsRunning = matchedPids.Count > 0;
            inst.RunningProcessIds = matchedPids;
        }
    }
}
