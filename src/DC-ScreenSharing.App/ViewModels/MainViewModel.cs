using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using DCScreenSharing.App.Services;
using DCScreenSharing.Core.Discord;
using DCScreenSharing.Core.Profiles;
using DCScreenSharing.Core.Settings;
using DCScreenSharing.Core.State;
using DCScreenSharing.Core.Updates;
using DCScreenSharing.Networking;
using DCScreenSharing.Shared;
using DCScreenSharing.Shared.Contracts;
using DCScreenSharing.Shared.Logging;

namespace DCScreenSharing.App.ViewModels;

public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}

public class MainViewModel : INotifyPropertyChanged
{
    private readonly IAppLogger _logger;
    private readonly DiscordLocator _discordLocator;
    private readonly DiscordProcessManager _discordProcessManager;
    private readonly ProfileRotationCoordinator _profileCoordinator;
    private readonly SecureProfileStore _profileStore;
    private readonly SettingsManager _settingsManager;
    private readonly ConnectionStateMachine _stateMachine;
    private readonly NetworkServiceClient _networkClient;
    private readonly ApplicationUpdateService _updateService;
    private readonly DiagnosticsService _diagnosticsService;
    private readonly TunnelHealthMonitor _healthMonitor;

    private UserSettings _settings;
    private DiscordInstallation? _detectedDiscord;
    private ServerEntry? _selectedServer;
    private TunnelConfiguration? _activeTunnelConfig;
    private string _statusText = "Disconnected";
    private string _statusDetail = "Ready to connect";
    private string _discordStatusText = "Detecting Discord...";
    private bool _isDiscordDetected;
    private bool _isBusy;
    private bool _isConnected;
    private string? _errorMessage;
    private string _connectButtonText = "Connect";

    // Activation State
    private bool _isActivated;
    private string _activationCode = string.Empty;
    private string? _activationError;
    private bool _isActivating;

    // Update State
    private bool _isUpdateAvailable;
    private string _updateTitle = string.Empty;
    private string _updateStatusText = string.Empty;
    private bool _isUpdating;
    private int _updateProgress;
    private UpdateCheckResult? _pendingUpdateInfo;

    public ObservableCollection<ServerEntry> Servers { get; } = new();

    public bool IsActivated
    {
        get => _isActivated;
        set => SetProperty(ref _isActivated, value);
    }

    public string ActivationCode
    {
        get => _activationCode;
        set => SetProperty(ref _activationCode, value);
    }

    public string? ActivationError
    {
        get => _activationError;
        set => SetProperty(ref _activationError, value);
    }

    public bool IsActivating
    {
        get => _isActivating;
        set => SetProperty(ref _isActivating, value);
    }

    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        set => SetProperty(ref _isUpdateAvailable, value);
    }

    public string UpdateTitle
    {
        get => _updateTitle;
        set => SetProperty(ref _updateTitle, value);
    }

    public string UpdateStatusText
    {
        get => _updateStatusText;
        set => SetProperty(ref _updateStatusText, value);
    }

    public bool IsUpdating
    {
        get => _isUpdating;
        set => SetProperty(ref _isUpdating, value);
    }

    public int UpdateProgress
    {
        get => _updateProgress;
        set => SetProperty(ref _updateProgress, value);
    }

    public ServerEntry? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (SetProperty(ref _selectedServer, value))
            {
                if (value != null)
                {
                    _settings.LastSelectedServerId = value.Id;
                    _settingsManager.Save(_settings);
                }
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string StatusDetail
    {
        get => _statusDetail;
        set => SetProperty(ref _statusDetail, value);
    }

    public string DiscordStatusText
    {
        get => _discordStatusText;
        set => SetProperty(ref _discordStatusText, value);
    }

    public bool IsDiscordDetected
    {
        get => _isDiscordDetected;
        set => SetProperty(ref _isDiscordDetected, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        set => SetProperty(ref _isConnected, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public string ConnectButtonText
    {
        get => _connectButtonText;
        set => SetProperty(ref _connectButtonText, value);
    }

    public ICommand ToggleConnectionCommand { get; }
    public ICommand RefreshDiscordCommand { get; }
    public ICommand RefreshCatalogCommand { get; }
    public ICommand ActivateCommand { get; }
    public ICommand ApplyUpdateCommand { get; }

    public MainViewModel(
        IAppLogger logger,
        DiscordLocator discordLocator,
        DiscordProcessManager discordProcessManager,
        ProfileRotationCoordinator profileCoordinator,
        SecureProfileStore profileStore,
        SettingsManager settingsManager,
        ConnectionStateMachine stateMachine,
        NetworkServiceClient networkClient,
        ApplicationUpdateService updateService,
        DiagnosticsService diagnosticsService,
        TunnelHealthMonitor? healthMonitor = null)
    {
        _logger = logger;
        _discordLocator = discordLocator;
        _discordProcessManager = discordProcessManager;
        _profileCoordinator = profileCoordinator;
        _profileStore = profileStore;
        _settingsManager = settingsManager;
        _stateMachine = stateMachine;
        _networkClient = networkClient;
        _updateService = updateService;
        _diagnosticsService = diagnosticsService;
        _healthMonitor = healthMonitor ?? new TunnelHealthMonitor(logger);

        _healthMonitor.HealthChanged += OnHealthChanged;
        _healthMonitor.OnPerformRecoveryAsync = PerformTunnelRecoveryAsync;

        _settings = _settingsManager.Load();

        ToggleConnectionCommand = new RelayCommand(async _ => await ToggleConnectionAsync(), _ => !IsBusy);
        RefreshDiscordCommand = new RelayCommand(_ => RefreshDiscord());
        RefreshCatalogCommand = new RelayCommand(async _ => await LoadServerCatalogAsync());
        ActivateCommand = new RelayCommand(async _ => await ActivateAsync(), _ => !IsActivating && !string.IsNullOrWhiteSpace(ActivationCode));
        ApplyUpdateCommand = new RelayCommand(async _ => await ApplyUpdateAsync(), _ => !IsUpdating && IsUpdateAvailable);

        _stateMachine.StateChanged += OnStateChanged;

        // Check if identity is already enrolled
        IsActivated = _profileCoordinator.ClientIdentity.IsEnrolled;
    }

    public async Task InitializeAsync()
    {
        _logger.Info("Initializing DC-ScreenSharing UI...");

        // 1. Detect Discord
        RefreshDiscord();

        // 2. If enrolled, load Server Catalog
        if (IsActivated)
        {
            await LoadServerCatalogAsync();
        }

        // 3. Background Check for Application Updates
        _ = CheckForUpdatesInBackgroundAsync();
    }

    public async Task ActivateAsync()
    {
        if (string.IsNullOrWhiteSpace(ActivationCode))
        {
            ActivationError = "Please enter an activation code.";
            return;
        }

        ActivationError = null;
        IsActivating = true;

        try
        {
            var (success, message) = await _profileCoordinator.EnrollWithTicketAsync(ActivationCode.Trim());
            if (success)
            {
                IsActivated = true;
                ActivationError = null;
                _logger.Info("Client activation completed successfully.");
                await LoadServerCatalogAsync();
            }
            else
            {
                if (message.Contains("expired", StringComparison.OrdinalIgnoreCase))
                    ActivationError = "Activation code expired.";
                else if (message.Contains("already been used", StringComparison.OrdinalIgnoreCase) || message.Contains("used", StringComparison.OrdinalIgnoreCase))
                    ActivationError = "Activation code already used.";
                else if (message.Contains("revoked", StringComparison.OrdinalIgnoreCase))
                    ActivationError = "Activation code revoked.";
                else if (message.Contains("contact", StringComparison.OrdinalIgnoreCase) || message.Contains("connection", StringComparison.OrdinalIgnoreCase))
                    ActivationError = "Unable to contact activation service.";
                else
                    ActivationError = "Invalid activation code.";
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Activation error", ex);
            ActivationError = "Unable to contact activation service.";
        }
        finally
        {
            IsActivating = false;
        }
    }

    public void RefreshDiscord()
    {
        _detectedDiscord = _discordLocator.ResolveInstallation(_settings.PreferredFlavor);
        if (_detectedDiscord != null)
        {
            IsDiscordDetected = true;
            DiscordStatusText = $"Discord detected ({_detectedDiscord.Flavor} v{_detectedDiscord.VersionString})";
            _logger.Info($"Discord identified: {_detectedDiscord}");
        }
        else
        {
            IsDiscordDetected = false;
            DiscordStatusText = "Discord not found";
            _logger.Warning("No Discord installation detected.");
        }
    }

    public async Task LoadServerCatalogAsync()
    {
        try
        {
            var catalog = await _profileCoordinator.FetchRemoteCatalogAsync();
            Servers.Clear();

            if (catalog != null && catalog.Servers.Count > 0)
            {
                foreach (var s in catalog.Servers.Where(s => s.Enabled))
                {
                    Servers.Add(s);
                }
            }

            if (!string.IsNullOrEmpty(_settings.LastSelectedServerId))
            {
                SelectedServer = Servers.FirstOrDefault(s => s.Id == _settings.LastSelectedServerId) ?? Servers.FirstOrDefault();
            }
            else
            {
                SelectedServer = Servers.FirstOrDefault();
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Could not refresh server catalog: {ex.Message}");
        }
    }

    private async Task ToggleConnectionAsync()
    {
        if (IsConnected)
        {
            await DisconnectAsync();
        }
        else
        {
            await ConnectAsync();
        }
    }

    private async Task ConnectAsync()
    {
        if (!IsActivated)
        {
            ErrorMessage = "Activation is required.";
            return;
        }

        if (SelectedServer == null)
        {
            ErrorMessage = "Please select a server first.";
            return;
        }

        ErrorMessage = null;
        IsBusy = true;
        var wasDiscordRunning = false;

        try
        {
            // Step 1: Checking Discord
            _stateMachine.TransitionTo(ConnectionState.Checking, "Checking Discord installation...");
            RefreshDiscord();
            if (_detectedDiscord == null)
            {
                ErrorMessage = "Discord was not detected on this system.";
                _stateMachine.TransitionTo(ConnectionState.Error, "Discord not found");
                return;
            }

            // Step 2: Checking NetworkService (Before closing Discord)
            _stateMachine.TransitionTo(ConnectionState.CheckingNetworkService, "Verifying DCSS.NetworkService...");
            var (serviceOk, serviceMsg) = await _networkClient.VerifyAndRecoverServiceAsync();
            if (!serviceOk)
            {
                ErrorMessage = serviceMsg;
                _logger.Error($"Network service pre-check failed: {serviceMsg}");
                _stateMachine.TransitionTo(ConnectionState.Error, serviceMsg);
                return;
            }

            // Step 3: Preparing Profile & Validating Configuration (Before closing Discord)
            _stateMachine.TransitionTo(ConnectionState.Preparing, "Preparing secure server profile...");
            var profile = await _profileCoordinator.GetOrRefreshProfileAsync(SelectedServer.Id);
            
            if (profile == null)
            {
                _logger.Warning($"Failed to acquire profile for server '{SelectedServer.Id}'. Checking authorization state.");
                // If profile was rejected due to client revocation, transition to activation required state
                _profileCoordinator.ClientIdentity.SetEnrolledClientId(string.Empty);
                IsActivated = false;
                ActivationError = "This installation needs to be activated again.";
                ErrorMessage = "This installation needs to be activated again.";
                _stateMachine.TransitionTo(ConnectionState.Error, "Activation required");
                return;
            }

            var isOvpn = VpnProtocol.IsOpenVpn(SelectedServer.Protocol) || (profile.Openvpn != null && profile.Wireguard == null);
            var tunnelConfig = new TunnelConfiguration
            {
                ServerId = profile.ServerId,
                ServerName = SelectedServer.Name,
                Protocol = isOvpn ? VpnProtocol.OpenVpn : VpnProtocol.WireGuard,
                DiscordExecutablePath = _detectedDiscord.ExecutablePath
            };

            if (isOvpn && profile.Openvpn != null)
            {
                var primaryRemote = profile.Openvpn.RemoteEndpoints.FirstOrDefault();
                tunnelConfig.Endpoint = primaryRemote?.Host ?? "127.0.0.1";
                tunnelConfig.Port = primaryRemote?.Port ?? 1194;
                tunnelConfig.OpenVpnProfileJson = System.Text.Json.JsonSerializer.Serialize(profile.Openvpn);
            }
            else if (profile.Wireguard != null)
            {
                tunnelConfig.Endpoint = profile.Wireguard.Endpoint;
                tunnelConfig.Port = profile.Wireguard.Port;
                tunnelConfig.Address = profile.Wireguard.Address;
                tunnelConfig.Addresses = new List<string>(profile.Wireguard.Addresses);
                tunnelConfig.Dns = profile.Wireguard.Dns;
                tunnelConfig.DnsServers = new List<string>(profile.Wireguard.DnsServers);
                tunnelConfig.PrivateKey = profile.Wireguard.PrivateKey;
                tunnelConfig.PeerPublicKey = profile.Wireguard.PeerPublicKey;
                tunnelConfig.AllowedIps = profile.Wireguard.AllowedIps;
                tunnelConfig.AllowedIpsList = new List<string>(profile.Wireguard.AllowedIpsList);
                tunnelConfig.Mtu = profile.Wireguard.Mtu;
                tunnelConfig.PersistentKeepalive = profile.Wireguard.PersistentKeepalive;
            }

            // Pre-validate engine configuration via NetworkService before any destructive action
            _stateMachine.TransitionTo(ConnectionState.Preparing, "Validating routing engine configuration...");
            var valResult = await _networkClient.ValidateConfigAsync(tunnelConfig);
            if (!valResult.Success)
            {
                _logger.Error($"Routing configuration pre-validation failed: {valResult.Message}");
                ErrorMessage = $"Routing configuration error: {valResult.Message}";
                _stateMachine.TransitionTo(ConnectionState.Error, "Invalid configuration");
                return;
            }

            // Step 4: Closing Discord if running
            wasDiscordRunning = _detectedDiscord.IsRunning;
            if (wasDiscordRunning)
            {
                _stateMachine.TransitionTo(ConnectionState.ClosingDiscord, $"Closing active Discord instance ({_detectedDiscord.Flavor})...");
                var closed = await _discordProcessManager.CloseDiscordGracefullyAsync(_detectedDiscord);
                if (!closed)
                {
                    _logger.Warning("Discord could not be closed completely, continuing with tunnel startup...");
                }
            }

            // Step 5: Starting Tunnel via NetworkService IPC
            _stateMachine.TransitionTo(ConnectionState.StartingTunnel, "Starting network tunnel...");
            _activeTunnelConfig = tunnelConfig;

            var tunnelResult = await _networkClient.StartTunnelAsync(tunnelConfig);
            if (!tunnelResult.Success)
            {
                _logger.Error($"StartTunnel failed: {tunnelResult.Message}. Performing clean recovery...");
                _activeTunnelConfig = null;
                
                // Cleanup partial tunnel state
                try { await _networkClient.StopTunnelAsync(); } catch { }

                // Relaunch Discord if it was running before
                if (wasDiscordRunning)
                {
                    _logger.Info($"Restoring previously detected Discord ({_detectedDiscord.Flavor})...");
                    _discordProcessManager.LaunchDiscord(_detectedDiscord);
                }

                ErrorMessage = $"Network service error: {tunnelResult.Message}";
                _stateMachine.TransitionTo(ConnectionState.Error, tunnelResult.Message);
                return;
            }

            // Step 5.5: Tunnel readiness settlement
            _stateMachine.TransitionTo(ConnectionState.StartingTunnel, "Verifying tunnel readiness...");
            await Task.Delay(800);

            // Step 6: Launching Discord
            if (_settings.AutoLaunchDiscord)
            {
                _stateMachine.TransitionTo(ConnectionState.LaunchingDiscord, $"Launching Discord ({_detectedDiscord.Flavor}) with tunnel route...");
                _discordProcessManager.LaunchDiscord(_detectedDiscord);
            }

            // Step 7: Connected
            _stateMachine.TransitionTo(ConnectionState.Connected, $"Connected to {SelectedServer.Name}");
            _healthMonitor.StartMonitoring(profile.Wireguard.Endpoint);
        }
        catch (Exception ex)
        {
            _logger.Error("Error during connect sequence", ex);
            _activeTunnelConfig = null;
            
            // Clean up partial tunnel state
            try { await _networkClient.StopTunnelAsync(); } catch { }

            // Restore Discord if it was closed during sequence
            if (wasDiscordRunning && _detectedDiscord != null)
            {
                try
                {
                    _logger.Info($"Restoring previously detected Discord ({_detectedDiscord.Flavor}) after exception...");
                    _discordProcessManager.LaunchDiscord(_detectedDiscord);
                }
                catch { }
            }

            ErrorMessage = $"Connection failed: {ex.Message}";
            _stateMachine.TransitionTo(ConnectionState.Error, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DisconnectAsync()
    {
        ErrorMessage = null;
        IsBusy = true;

        try
        {
            _healthMonitor.StopMonitoring();
            _activeTunnelConfig = null;

            _stateMachine.TransitionTo(ConnectionState.Disconnecting, "Disconnecting tunnel...");
            await _networkClient.StopTunnelAsync();
            _stateMachine.TransitionTo(ConnectionState.Disconnected, "Disconnected");
        }
        catch (Exception ex)
        {
            _logger.Error("Error during disconnect", ex);
            _stateMachine.TransitionTo(ConnectionState.Disconnected);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnHealthChanged(object? sender, HealthChangedEventArgs e)
    {
        if (!_isConnected) return;

        Application.Current?.Dispatcher.Invoke(() =>
        {
            switch (e.Report.State)
            {
                case TunnelHealthState.Healthy:
                    StatusText = "Connected";
                    StatusDetail = e.Report.MedianLatencyMs.HasValue
                        ? $"Connected to {SelectedServer?.Name} ({e.Report.MedianLatencyMs} ms)"
                        : $"Connected to {SelectedServer?.Name}";
                    break;
                case TunnelHealthState.Degraded:
                    StatusText = "Connected";
                    StatusDetail = e.Report.MedianLatencyMs.HasValue
                        ? $"Connection degraded ({e.Report.MedianLatencyMs} ms)"
                        : "Connection degraded";
                    break;
                case TunnelHealthState.Recovering:
                    StatusText = "Reconnecting...";
                    StatusDetail = e.Report.Message;
                    break;
                case TunnelHealthState.Unavailable:
                    StatusText = "Reconnecting...";
                    StatusDetail = "Connection lost. Reconnecting...";
                    break;
            }
        });
    }

    private async Task<bool> PerformTunnelRecoveryAsync()
    {
        if (_activeTunnelConfig == null || !_isConnected) return false;

        _logger.Info("[Recovery] Performing clean tunnel self-recovery...");
        try
        {
            // 1. Stop current tunnel instance
            await _networkClient.StopTunnelAsync();

            // 2. Short backoff
            await Task.Delay(1500);

            // 3. Verify service
            var (ok, _) = await _networkClient.VerifyAndRecoverServiceAsync();
            if (!ok) return false;

            // 4. Restart tunnel with active config
            var result = await _networkClient.StartTunnelAsync(_activeTunnelConfig);
            if (!result.Success)
            {
                _logger.Warning($"[Recovery] StartTunnel returned failure: {result.Message}");
                return false;
            }

            // 5. Readiness settle
            await Task.Delay(1000);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("[Recovery] Exception during recovery", ex);
            return false;
        }
    }

    private void OnStateChanged(object? sender, StateChangedEventArgs e)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            StatusText = e.CurrentState.ToString();
            StatusDetail = e.Message;
            IsConnected = e.CurrentState == ConnectionState.Connected;
            ConnectButtonText = IsConnected ? "Disconnect" : "Connect";
            IsBusy = _stateMachine.IsBusy;
        });
    }

    private async Task CheckForUpdatesInBackgroundAsync()
    {
        try
        {
            var result = await _updateService.CheckForUpdatesAsync(Version.Parse(Constants.CurrentVersion));
            if (result.UpdateAvailable)
            {
                _logger.Info($"Application update available: v{result.LatestVersion}");
                _pendingUpdateInfo = result;

                Application.Current?.Dispatcher.Invoke(() =>
                {
                    IsUpdateAvailable = true;
                    UpdateTitle = $"Update available: DC-ScreenSharing v{result.LatestVersion}";
                    UpdateStatusText = "A new update is ready to install.";
                });
            }
            else
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    IsUpdateAvailable = false;
                });
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Background update check error: {ex.Message}");
        }
    }

    private bool _updateAttemptedThisSession = false;

    public async Task ApplyUpdateAsync()
    {
        if (_updateAttemptedThisSession || _pendingUpdateInfo == null || !_pendingUpdateInfo.UpdateAvailable)
            return;

        _updateAttemptedThisSession = true;
        IsUpdating = true;
        UpdateStatusText = "Downloading update (0%)...";
        UpdateProgress = 0;

        try
        {
            var progress = new Progress<int>(pct =>
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    UpdateProgress = pct;
                    UpdateStatusText = $"Downloading update ({pct}%)...";
                });
            });

            var installerPath = await _updateService.DownloadAndVerifyUpdateAsync(_pendingUpdateInfo, progress);
            if (string.IsNullOrEmpty(installerPath) || !File.Exists(installerPath))
            {
                ErrorMessage = "Update could not be installed. Please restart DC-ScreenSharing and try again.";
                UpdateStatusText = "Update could not be installed. Please restart DC-ScreenSharing and try again.";
                IsUpdating = false;
                IsUpdateAvailable = false;
                return;
            }

            UpdateStatusText = "Preparing update installation...";

            // If tunnel is active, disconnect cleanly before applying update
            if (_isConnected)
            {
                _logger.Info("Disconnecting active tunnel before applying update...");
                await DisconnectAsync();
            }

            _logger.Info($"Launching updater coordinator with package: {installerPath}");
            var launched = _updateService.LaunchUpdater(installerPath);
            if (launched)
            {
                _logger.Info("Updater process started. Shutting down main application for update.");
                Application.Current?.Dispatcher.Invoke(() => Application.Current.Shutdown());
            }
            else
            {
                ErrorMessage = "Update could not be installed. Please restart DC-ScreenSharing and try again.";
                UpdateStatusText = "Update could not be installed. Please restart DC-ScreenSharing and try again.";
                IsUpdating = false;
                IsUpdateAvailable = false;
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Exception while applying update", ex);
            ErrorMessage = "Update could not be installed. Please restart DC-ScreenSharing and try again.";
            UpdateStatusText = "Update could not be installed. Please restart DC-ScreenSharing and try again.";
            IsUpdating = false;
            IsUpdateAvailable = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
            return false;

        storage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
