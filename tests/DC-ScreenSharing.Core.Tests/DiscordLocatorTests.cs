using DCScreenSharing.Core.Discord;
using Xunit;

namespace DCScreenSharing.Core.Tests;

public class DiscordLocatorTests
{
    [Fact]
    public void DiscoverAllInstallations_DoesNotThrow()
    {
        var locator = new DiscordLocator();
        var installs = locator.DiscoverAllInstallations();
        Assert.NotNull(installs);
    }

    [Fact]
    public void ResolveInstallation_ReturnsMatchingFlavorOrNull()
    {
        var locator = new DiscordLocator();
        var resolved = locator.ResolveInstallation(DiscordFlavor.Stable);
        if (resolved != null)
        {
            Assert.Equal(DiscordFlavor.Stable, resolved.Flavor);
            Assert.True(File.Exists(resolved.ExecutablePath) || Directory.Exists(resolved.InstallationDirectory));
        }
    }

    [Fact]
    public void VersionParsing_OrdersCorrectly()
    {
        var v1 = new Version("1.0.900");
        var v2 = new Version("1.0.9254");
        var v3 = new Version("1.0.10000");

        Assert.True(v2 > v1);
        Assert.True(v3 > v2);
    }
}
