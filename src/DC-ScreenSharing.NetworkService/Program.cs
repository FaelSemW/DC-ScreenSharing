using System.IO;
using System.ServiceProcess;
using DCScreenSharing.Networking;
using DCScreenSharing.Shared;
using DCScreenSharing.Shared.Logging;

namespace DCScreenSharing.NetworkService;

public class NetworkWindowsService : ServiceBase
{
    private readonly IpcServer _server;
    private readonly ProcessRoutingEngine _engine;
    private readonly CrashRecoveryManager _recovery;
    private readonly IAppLogger _logger;

    public NetworkWindowsService(IpcServer server, ProcessRoutingEngine engine, CrashRecoveryManager recovery, IAppLogger logger)
    {
        ServiceName = "DCSS.NetworkService";
        _server = server;
        _engine = engine;
        _recovery = recovery;
        _logger = logger;
    }

    protected override void OnStart(string[] args)
    {
        _logger.Info("ServiceBase.OnStart: Initializing DCSS.NetworkService...");
        try
        {
            _recovery.PerformStartupRecovery(_engine);
            _server.Start();
            _logger.Info("ServiceBase.OnStart: Service successfully started and listening.");
        }
        catch (Exception ex)
        {
            _logger.Error("ServiceBase.OnStart failed to start service components", ex);
            throw;
        }
    }

    protected override void OnStop()
    {
        _logger.Info("ServiceBase.OnStop: Stopping DCSS.NetworkService...");
        try
        {
            _server.Stop();
            _logger.Info("ServiceBase.OnStop: Service successfully stopped.");
        }
        catch (Exception ex)
        {
            _logger.Error("ServiceBase.OnStop error", ex);
        }
    }

    protected override void OnShutdown()
    {
        _logger.Info("ServiceBase.OnShutdown: System shutting down, cleaning up service...");
        OnStop();
    }
}

public static class Program
{
    public static void Main(string[] args)
    {
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DC-ScreenSharing", "logs");
        var logger = new FileLogger(logDir, "service.log");

        logger.Info("=== DC-ScreenSharing Network Service Entry Point ===");
        logger.Info($"OS: {Environment.OSVersion}, 64-bit OS: {Environment.Is64BitOperatingSystem}, UserInteractive: {Environment.UserInteractive}");

        var engine = new ProcessRoutingEngine(logger);
        var recovery = new CrashRecoveryManager(logger);
        var server = new IpcServer(engine, recovery, logger);

        var isConsoleMode = Environment.UserInteractive || args.Contains("--console");

        if (isConsoleMode)
        {
            logger.Info("Running in console/interactive mode.");
            recovery.PerformStartupRecovery(engine);
            server.Start();

            Console.WriteLine("DCSS Network Service is running in console mode. Press Ctrl+C to terminate.");
            var exitEvent = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                exitEvent.Set();
            };
            exitEvent.Wait();

            server.Stop();
            logger.Info("=== DC-ScreenSharing Network Service Stopped (Console) ===");
        }
        else
        {
            logger.Info("Registering with Windows Service Control Manager (SCM)...");
            ServiceBase.Run(new NetworkWindowsService(server, engine, recovery, logger));
        }
    }
}
