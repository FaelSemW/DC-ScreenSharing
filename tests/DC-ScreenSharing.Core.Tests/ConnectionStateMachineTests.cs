using DCScreenSharing.Core.State;
using DCScreenSharing.Shared.Logging;
using Xunit;

namespace DCScreenSharing.Core.Tests;

public class ConnectionStateMachineTests
{
    private readonly ConnectionStateMachine _stateMachine;

    public ConnectionStateMachineTests()
    {
        var logger = new FileLogger(Path.GetTempPath());
        _stateMachine = new ConnectionStateMachine(logger);
    }

    [Fact]
    public void InitialState_IsDisconnected()
    {
        Assert.Equal(ConnectionState.Disconnected, _stateMachine.CurrentState);
        Assert.False(_stateMachine.IsConnected);
        Assert.False(_stateMachine.IsBusy);
    }

    [Fact]
    public void ValidTransitions_Succeed()
    {
        Assert.True(_stateMachine.TransitionTo(ConnectionState.Checking));
        Assert.True(_stateMachine.TransitionTo(ConnectionState.Preparing));
        Assert.True(_stateMachine.TransitionTo(ConnectionState.ClosingDiscord));
        Assert.True(_stateMachine.TransitionTo(ConnectionState.StartingTunnel));
        Assert.True(_stateMachine.TransitionTo(ConnectionState.LaunchingDiscord));
        Assert.True(_stateMachine.TransitionTo(ConnectionState.Connecting));
        Assert.True(_stateMachine.TransitionTo(ConnectionState.Connected));
        Assert.True(_stateMachine.IsConnected);

        Assert.True(_stateMachine.TransitionTo(ConnectionState.Disconnecting));
        Assert.True(_stateMachine.TransitionTo(ConnectionState.Disconnected));
        Assert.False(_stateMachine.IsConnected);
    }

    [Fact]
    public void InvalidTransition_FromDisconnectedToConnected_Fails()
    {
        Assert.False(_stateMachine.TransitionTo(ConnectionState.Connected));
        Assert.Equal(ConnectionState.Disconnected, _stateMachine.CurrentState);
    }

    [Fact]
    public void CanTransitionToError_FromAnyState()
    {
        _stateMachine.TransitionTo(ConnectionState.Checking);
        Assert.True(_stateMachine.TransitionTo(ConnectionState.Error, "Network unreachable"));
        Assert.Equal(ConnectionState.Error, _stateMachine.CurrentState);

        // Can recover from Error back to Disconnected
        Assert.True(_stateMachine.TransitionTo(ConnectionState.Disconnected));
        Assert.Equal(ConnectionState.Disconnected, _stateMachine.CurrentState);
    }
}
