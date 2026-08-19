using System.IO;
using System.Windows;
using DCScreenSharing.App.Services;
using DCScreenSharing.App.ViewModels;
using DCScreenSharing.App.Views;
using DCScreenSharing.Core.Discord;
using DCScreenSharing.Core.Profiles;
using DCScreenSharing.Core.Settings;
using DCScreenSharing.Core.State;
using DCScreenSharing.Core.Updates;
using DCScreenSharing.Networking;
using DCScreenSharing.Shared;
using DCScreenSharing.Shared.Logging;

namespace DCScreenSharing.App;

public partial class App : Application
{
    private Mutex? _appMutex;
    public static IAppLogger Logger { get; private set; } = null!;
    public static MainViewModel ViewModel { get; private set; } = null!;
    public static SettingsManager SettingsManager { get; private set; } = null!;
    public static DiagnosticsService DiagnosticsService { get; private set; } = null!;

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        // Enforce single instance
        _appMutex = new Mutex(true, Constants.AppMutexName, out var isNewInstance);
        if (!isNewInstance)
        {
            MessageBox.Show("DC-ScreenSharing is already running.", "DC-ScreenSharing", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DC-ScreenSharing", "logs");
        Logger = new FileLogger(logDir, "app.log");

        Logger.Info("=== DC-ScreenSharing Application Starting ===");
        Logger.Info($"OS: {Environment.OSVersion}, 64-bit OS: {Environment.Is64BitOperatingSystem}");

        SettingsManager = new SettingsManager(null, Logger);
        var discordLocator = new DiscordLocator();
        var discordProcessManager = new DiscordProcessManager(Logger);
        var secureStore = new SecureProfileStore(null, Logger);
        var rotationCoordinator = new ProfileRotationCoordinator(secureStore, Logger);
        var stateMachine = new ConnectionStateMachine(Logger);
        var networkClient = new NetworkServiceClient(Logger);
        var updateService = new ApplicationUpdateService(Logger);
        var healthMonitor = new TunnelHealthMonitor(Logger);
        DiagnosticsService = new DiagnosticsService(Logger, networkClient, discordLocator);

        ViewModel = new MainViewModel(
            Logger,
            discordLocator,
            discordProcessManager,
            rotationCoordinator,
            secureStore,
            SettingsManager,
            stateMachine,
            networkClient,
            updateService,
            DiagnosticsService,
            healthMonitor);

        var mainWindow = new MainWindow(ViewModel);
        mainWindow.Show();

        await ViewModel.InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _appMutex?.ReleaseMutex();
        _appMutex?.Dispose();
        base.OnExit(e);
    }
}
