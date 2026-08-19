using DCScreenSharing.Core.Profiles;
using Xunit;

namespace DCScreenSharing.Core.Tests;

public class SecureProfileStoreTests : IDisposable
{
    private readonly string _testDir;
    private readonly SecureProfileStore _store;

    public SecureProfileStoreTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "DCSS_TestStore_" + Guid.NewGuid().ToString("N"));
        _store = new SecureProfileStore(_testDir);
    }

    [Fact]
    public void SaveAndLoadProfile_EncryptsAndDecryptsAccurately()
    {
        var profile = new ServerProfile
        {
            ServerId = "us-test",
            Generation = 100,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            Wireguard = new WireGuardProfileConfig
            {
                Endpoint = "192.0.2.1",
                Port = 51820,
                PrivateKey = "testprivatekey12345678901234567890123456789="
            }
        };

        var saved = _store.SaveProfile(profile);
        Assert.True(saved);

        var loaded = _store.LoadProfile("us-test");
        Assert.NotNull(loaded);
        Assert.Equal("us-test", loaded.ServerId);
        Assert.Equal(100, loaded.Generation);
        Assert.Equal("192.0.2.1", loaded.Wireguard.Endpoint);
        Assert.Equal("testprivatekey12345678901234567890123456789=", loaded.Wireguard.PrivateKey);
    }

    [Fact]
    public void RollbackProfile_RestoresPreviousGeneration()
    {
        var gen1 = new ServerProfile { ServerId = "rollback-test", Generation = 1 };
        var gen2 = new ServerProfile { ServerId = "rollback-test", Generation = 2 };

        _store.SaveProfile(gen1);
        _store.SaveProfile(gen2);

        var loadedGen2 = _store.LoadProfile("rollback-test");
        Assert.Equal(2, loadedGen2?.Generation);

        var rolledBack = _store.RollbackProfile("rollback-test");
        Assert.NotNull(rolledBack);
        Assert.Equal(1, rolledBack.Generation);
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
