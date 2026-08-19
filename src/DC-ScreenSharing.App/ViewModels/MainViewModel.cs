using System.Collections.ObjectModel;
using System.ComponentModel;
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

    private UserSettings _settings;
    private DiscordInstallation? _detectedDiscord;
    private ServerEntry? _selectedServer;
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
        DiagnosticsService diagnosticsService)
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

        _settings = _settingsManager.Load();

        ToggleConnectionCommand = new RelayCommand(async _ => await ToggleConnectionAsync(), _ => !IsBusy);
        RefreshDiscordCommand = new RelayCommand(_ => RefreshDiscord());
        RefreshCatalogCommand = new RelayCommand(async _ => await LoadServerCatalogAsync());
        ActivateCommand = new RelayCommand(async _ => await ActivateAsync(), _ => !IsActivating && !string.IsNullOrWhiteSpace(ActivationCode));

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

            // Step 3: Preparing Profile
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
            var tunnelConfig = new TunnelConfiguration
            {
                ServerId = profile.ServerId,
                ServerName = SelectedServer.Name,
                Endpoint = profile.Wireguard.Endpoint,
                Port = profile.Wireguard.Port,
                Address = profile.Wireguard.Address,
                Dns = profile.Wireguard.Dns,
                PrivateKey = profile.Wireguard.PrivateKey,
                PeerPublicKey = profile.Wireguard.PeerPublicKey,
                AllowedIps = profile.Wireguard.AllowedIps,
                Mtu = profile.Wireguard.Mtu,
                DiscordExecutablePath = _detectedDiscord.ExecutablePath
            };

            var tunnelResult = await _networkClient.StartTunnelAsync(tunnelConfig);
            if (!tunnelResult.Success)
            {
                _logger.Error($"StartTunnel failed: {tunnelResult.Message}. Performing clean recovery...");
                
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

            // Step 6: Launching Discord
            if (_settings.AutoLaunchDiscord)
            {
                _stateMachine.TransitionTo(ConnectionState.LaunchingDiscord, $"Launching Discord ({_detectedDiscord.Flavor}) with tunnel route...");
                _discordProcessManager.LaunchDiscord(_detectedDiscord);
            }

            // Step 7: Connected
            _stateMachine.TransitionTo(ConnectionState.Connected, $"Connected to {SelectedServer.Name}");
        }
        catch (Exception ex)
        {
            _logger.Error("Error during connect sequence", ex);
            
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
            }
        }
        catch { }
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
