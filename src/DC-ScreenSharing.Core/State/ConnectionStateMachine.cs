using DCScreenSharing.Shared.Logging;

namespace DCScreenSharing.Core.State;

public enum ConnectionState
{
    Disconnected = 0,
    Checking = 1,
    CheckingNetworkService = 2,
    Preparing = 3,
    ClosingDiscord = 4,
    StartingTunnel = 5,
    LaunchingDiscord = 6,
    Connecting = 7,
    Connected = 8,
    Disconnecting = 9,
    Updating = 10,
    Error = 11
}

public class StateChangedEventArgs : EventArgs
{
    public ConnectionState PreviousState { get; }
    public ConnectionState CurrentState { get; }
    public string Message { get; }

    public StateChangedEventArgs(ConnectionState prev, ConnectionState current, string message)
    {
        PreviousState = prev;
        CurrentState = current;
        Message = message;
    }
}

public class ConnectionStateMachine
{
    private readonly object _lock = new();
    private readonly IAppLogger _logger;
    private ConnectionState _currentState = ConnectionState.Disconnected;
    private string _statusMessage = "Disconnected";

    public event EventHandler<StateChangedEventArgs>? StateChanged;

    public ConnectionStateMachine(IAppLogger logger)
    {
        _logger = logger;
    }

    public ConnectionState CurrentState
    {
        get { lock (_lock) return _currentState; }
    }

    public string StatusMessage
    {
        get { lock (_lock) return _statusMessage; }
    }

    public bool IsConnected => CurrentState == ConnectionState.Connected;
    public bool IsBusy => CurrentState is not (ConnectionState.Disconnected or ConnectionState.Connected or ConnectionState.Error);

    public bool TransitionTo(ConnectionState newState, string message = "")
    {
        ConnectionState previous;
        lock (_lock)
        {
            if (!IsValidTransition(_currentState, newState))
            {
                _logger.Warning($"Invalid state transition attempted: {_currentState} -> {newState}");
                return false;
            }

            previous = _currentState;
            _currentState = newState;
            _statusMessage = string.IsNullOrEmpty(message) ? GetDefaultMessage(newState) : message;
            _logger.Info($"State transition: {previous} -> {newState} ({_statusMessage})");
        }

        StateChanged?.Invoke(this, new StateChangedEventArgs(previous, newState, _statusMessage));
        return true;
    }

    private static bool IsValidTransition(ConnectionState from, ConnectionState to)
    {
        if (to == ConnectionState.Error)
            return true; // Any state can transition to Error on failure

        return from switch
        {
            ConnectionState.Disconnected => to is ConnectionState.Checking or ConnectionState.Preparing or ConnectionState.Updating,
            ConnectionState.Checking => to is ConnectionState.CheckingNetworkService or ConnectionState.Preparing or ConnectionState.Disconnected or ConnectionState.Updating,
            ConnectionState.CheckingNetworkService => to is ConnectionState.Preparing or ConnectionState.Disconnected or ConnectionState.Updating,
            ConnectionState.Preparing => to is ConnectionState.ClosingDiscord or ConnectionState.StartingTunnel or ConnectionState.Disconnected,
            ConnectionState.ClosingDiscord => to is ConnectionState.StartingTunnel or ConnectionState.Disconnected,
            ConnectionState.StartingTunnel => to is ConnectionState.LaunchingDiscord or ConnectionState.Connecting or ConnectionState.Disconnecting,
            ConnectionState.LaunchingDiscord => to is ConnectionState.Connecting or ConnectionState.Connected or ConnectionState.Disconnecting,
            ConnectionState.Connecting => to is ConnectionState.Connected or ConnectionState.Disconnecting,
            ConnectionState.Connected => to is ConnectionState.Disconnecting,
            ConnectionState.Disconnecting => to is ConnectionState.Disconnected,
            ConnectionState.Updating => to is ConnectionState.Disconnected,
            ConnectionState.Error => to is ConnectionState.Disconnected or ConnectionState.Checking,
            _ => false
        };
    }

    private static string GetDefaultMessage(ConnectionState state) => state switch
    {
        ConnectionState.Disconnected => "Disconnected",
        ConnectionState.Checking => "Checking system...",
        ConnectionState.CheckingNetworkService => "Verifying network service...",
        ConnectionState.Preparing => "Preparing server profile...",
        ConnectionState.ClosingDiscord => "Closing Discord...",
        ConnectionState.StartingTunnel => "Starting network tunnel...",
        ConnectionState.LaunchingDiscord => "Launching Discord...",
        ConnectionState.Connecting => "Associating network route...",
        ConnectionState.Connected => "Connected",
        ConnectionState.Disconnecting => "Disconnecting...",
        ConnectionState.Updating => "Updating application...",
        ConnectionState.Error => "Connection error",
        _ => state.ToString()
    };
}
