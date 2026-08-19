using System.Diagnostics;
using DCScreenSharing.Shared.Logging;

namespace DCScreenSharing.Updater;

public static class Program
{
    public static void Main(string[] args)
    {
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DC-ScreenSharing", "logs");
        var logger = new FileLogger(logDir, "updater.log");

        logger.Info("=== DC-ScreenSharing Updater Started ===");

        string? stagedPath = null;
        int targetPid = 0;
        string? relaunchPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--staged" && i + 1 < args.Length)
                stagedPath = args[++i];
            else if (args[i] == "--target-pid" && i + 1 < args.Length && int.TryParse(args[++i], out var pid))
                targetPid = pid;
            else if (args[i] == "--relaunch" && i + 1 < args.Length)
                relaunchPath = args[++i];
        }

        if (string.IsNullOrEmpty(stagedPath) || !File.Exists(stagedPath))
        {
            logger.Error("Staged update package not specified or does not exist. Aborting.");
            return;
        }

        // Wait for parent application process to exit
        if (targetPid > 0)
        {
            try
            {
                logger.Info($"Waiting for parent process PID {targetPid} to exit...");
                var parentProc = Process.GetProcessById(targetPid);
                parentProc.WaitForExit(10000);
            }
            catch (Exception ex)
            {
                logger.Warning($"Parent process check note: {ex.Message}");
            }
        }

        try
        {
            logger.Info($"Executing installer: {stagedPath} /SILENT /NORESTART");
            var psi = new ProcessStartInfo
            {
                FileName = stagedPath,
                Arguments = "/SILENT /NORESTART /CLOSEAPPLICATIONS",
                UseShellExecute = true
            };

            var installProc = Process.Start(psi);
            installProc?.WaitForExit(60000);
            logger.Info($"Installer finished with exit code: {installProc?.ExitCode}");

            // Clean up staged installer file
            try { File.Delete(stagedPath); } catch { }

            // Relaunch the updated main application
            if (!string.IsNullOrEmpty(relaunchPath) && File.Exists(relaunchPath))
            {
                logger.Info($"Relaunching main application: {relaunchPath}");
                Process.Start(new ProcessStartInfo
                {
                    FileName = relaunchPath,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            logger.Error("Failed to apply update", ex);
        }

        logger.Info("=== DC-ScreenSharing Updater Finished ===");
    }
}
