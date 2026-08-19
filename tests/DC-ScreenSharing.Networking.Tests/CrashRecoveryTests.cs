using DCScreenSharing.Networking;
using DCScreenSharing.Shared.Logging;
using Xunit;

namespace DCScreenSharing.Networking.Tests;

public class CrashRecoveryTests : IDisposable
{
    private readonly string _testDir;
    private readonly CrashRecoveryManager _recovery;
    private readonly ProcessRoutingEngine _engine;

    public CrashRecoveryTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "DCSS_RecoveryTest_" + Guid.NewGuid().ToString("N"));
        var logger = new FileLogger(_testDir);
        _recovery = new CrashRecoveryManager(logger, _testDir);
        _engine = new ProcessRoutingEngine(logger, _testDir);
    }

    [Fact]
    public void RecordState_And_ClearState_HandlesCycleCleanly()
    {
        _recovery.RecordState(true, "us-01", "dcss-wintun", 1234);
        var stateFile = Path.Combine(_testDir, "net_state.json");
        Assert.True(File.Exists(stateFile));

        _recovery.PerformStartupRecovery(_engine);
        Assert.False(File.Exists(stateFile));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch { }
    }
}
