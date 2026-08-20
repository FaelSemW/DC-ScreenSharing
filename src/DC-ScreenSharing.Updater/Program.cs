using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using DCScreenSharing.Shared.Logging;

namespace DCScreenSharing.Updater;

public static class Program
{
    public static void Main(string[] args)
    {
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DC-ScreenSharing", "logs");
        var logger = new FileLogger(logDir, "updater.log");

        var currentExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "unknown";
        logger.Info($"=== DC-ScreenSharing Updater Started (PID: {Environment.ProcessId}, Location: {currentExe}) ===");

        string? stagedPath = null;
        int targetPid = 0;
        string? relaunchPath = null;
        string? runtimeDir = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--staged" && i + 1 < args.Length)
                stagedPath = args[++i];
            else if (args[i] == "--target-pid" && i + 1 < args.Length && int.TryParse(args[++i], out var pid))
                targetPid = pid;
            else if (args[i] == "--relaunch" && i + 1 < args.Length)
                relaunchPath = args[++i];
            else if (args[i] == "--runtime-dir" && i + 1 < args.Length)
                runtimeDir = args[++i];
        }

        if (string.IsNullOrEmpty(stagedPath) || !File.Exists(stagedPath))
        {
            logger.Error($"Staged update package not specified or does not exist: '{stagedPath}'. Aborting.");
            return;
        }

        // 1. Wait for parent application process (DC-ScreenSharing.exe) to exit cleanly
        if (targetPid > 0)
        {
            try
            {
                logger.Info($"Waiting for parent process PID {targetPid} to exit...");
                var parentProc = Process.GetProcessById(targetPid);
                if (!parentProc.WaitForExit(15000))
                {
                    logger.Warning($"Parent process PID {targetPid} did not exit in 15s. Terminating parent process...");
                    try { parentProc.Kill(entireProcessTree: true); } catch { }
                    parentProc.WaitForExit(3000);
                }
                logger.Info("Parent process exited.");
            }
            catch (ArgumentException)
            {
                logger.Info($"Parent process PID {targetPid} already exited.");
            }
            catch (Exception ex)
            {
                logger.Warning($"Parent process wait note: {ex.Message}");
            }
        }

        // 2. Stop DCSS.NetworkService cleanly before replacing binaries
        StopNetworkService(logger);

        // 3. Terminate any remaining DCSS-owned processes
        TerminateRemainingDcssProcesses(logger);

        // 4. Verify file locks are cleared in the installation directory
        var installDir = !string.IsNullOrEmpty(relaunchPath) ? Path.GetDirectoryName(relaunchPath) : @"C:\Program Files\DC-ScreenSharing";
        if (!string.IsNullOrEmpty(installDir) && Directory.Exists(installDir))
        {
            WaitForFilesUnlocked(installDir, logger);
        }

        // 5. Execute Inno Setup installer silently
        try
        {
            logger.Info($"Executing installer: {stagedPath} /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CLOSEAPPLICATIONS");
            var psi = new ProcessStartInfo
            {
                FileName = stagedPath,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CLOSEAPPLICATIONS",
                UseShellExecute = true
            };

            var installProc = Process.Start(psi);
            if (installProc == null)
            {
                logger.Error("Failed to start installer process.");
                return;
            }

            var finished = installProc.WaitForExit(120000); // 2 min max
            var exitCode = finished ? installProc.ExitCode : -1;
            logger.Info($"Installer finished: Completed={finished}, ExitCode={exitCode}");

            if (exitCode != 0)
            {
                logger.Error($"Installer failed with exit code {exitCode}. Aborting relaunch.");
                return;
            }

            // Clean up staged installer file after successful installation
            try { File.Delete(stagedPath); } catch { }

            // 6. Verify and start DCSS.NetworkService if not running
            StartNetworkService(logger);

            // 7. Relaunch the updated main application
            if (!string.IsNullOrEmpty(relaunchPath) && File.Exists(relaunchPath))
            {
                logger.Info($"Relaunching updated main application: {relaunchPath}");
                Process.Start(new ProcessStartInfo
                {
                    FileName = relaunchPath,
                    WorkingDirectory = Path.GetDirectoryName(relaunchPath),
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            logger.Error("Exception during update execution", ex);
        }

        logger.Info("=== DC-ScreenSharing Updater Finished ===");
    }

    private static void StopNetworkService(IAppLogger logger)
    {
        try
        {
            logger.Info("Stopping DCSS.NetworkService service...");
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = "stop DCSS.NetworkService",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit(5000);

            // Poll service status for up to 10 seconds
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 10000)
            {
                if (IsServiceStopped("DCSS.NetworkService"))
                {
                    logger.Info("DCSS.NetworkService successfully stopped.");
                    return;
                }
                Thread.Sleep(500);
            }
            logger.Warning("DCSS.NetworkService did not report stopped in 10s. Continuing...");
        }
        catch (Exception ex)
        {
            logger.Warning($"Could not stop service via sc.exe: {ex.Message}");
        }
    }

    private static void StartNetworkService(IAppLogger logger)
    {
        try
        {
            logger.Info("Verifying DCSS.NetworkService service startup...");
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = "start DCSS.NetworkService",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            logger.Warning($"Could not start service via sc.exe: {ex.Message}");
        }
    }

    private static bool IsServiceStopped(string serviceName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"query {serviceName}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return true;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);
            return output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private static void TerminateRemainingDcssProcesses(IAppLogger logger)
    {
        var currentPid = Environment.ProcessId;
        var processNames = new[] { "DC-ScreenSharing", "DCSS.NetworkService", "dcss-engine" };

        foreach (var name in processNames)
        {
            try
            {
                var procs = Process.GetProcessesByName(name);
                foreach (var p in procs)
                {
                    if (p.Id == currentPid) continue;
                    try
                    {
                        logger.Info($"Terminating lingering process {p.ProcessName} (PID: {p.Id})...");
                        p.Kill(entireProcessTree: true);
                        p.WaitForExit(3000);
                    }
                    catch (Exception ex)
                    {
                        logger.Warning($"Could not terminate process {p.ProcessName} (PID: {p.Id}): {ex.Message}");
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }
            catch { }
        }
    }

    private static void WaitForFilesUnlocked(string directory, IAppLogger logger)
    {
        var criticalFiles = new[]
        {
            "clrjit.dll",
            "coreclr.dll",
            "hostfxr.dll",
            "hostpolicy.dll",
            "DC-ScreenSharing.exe",
            "DCSS.NetworkService.exe"
        };

        logger.Info($"Checking file lock status for critical files in '{directory}'...");

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 8000)
        {
            bool allUnlocked = true;
            foreach (var rel in criticalFiles)
            {
                var fullPath = Path.Combine(directory, rel);
                if (!File.Exists(fullPath)) continue;

                if (!IsFileWritable(fullPath))
                {
                    allUnlocked = false;
                    logger.Debug($"File is currently locked: {rel}");
                    break;
                }
            }

            if (allUnlocked)
            {
                logger.Info("All critical installation files are unlocked and ready for replacement.");
                return;
            }

            Thread.Sleep(400);
        }

        logger.Warning("Proceeding to installation after lock check timeout.");
    }

    private static bool IsFileWritable(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // If read-only or permission-locked
            return false;
        }
        catch
        {
            return true;
        }
    }
}
