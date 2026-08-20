using System.IO;
using System.Security.Cryptography;
using System.Text;
using DCSS.ProfileCollector.Models;
using DCSS.ProfileCollector.Services;
using DCSS.ProfileCollector.ViewModels;
using Xunit;

namespace DCScreenSharing.IntegrationTests;

public class ProtonCollectorServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _inventoryPath;
    private readonly string _configsRoot;

    public ProtonCollectorServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "DCSS_ProtonTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _inventoryPath = Path.Combine(_testDir, "inventory.json");
        _configsRoot = Path.Combine(_testDir, "configs");
        Directory.CreateDirectory(_configsRoot);
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

    [Fact]
    public void ProtonVpn_DefaultOptions_AndRegions_AreValid()
    {
        // 1. Regions
        var regions = ProtonVpnAutomationService.GetDefaultRegions();
        Assert.NotEmpty(regions);
        Assert.Contains(regions, r => r.Code == "US" && r.DisplayName == "United States");
        Assert.Contains(regions, r => r.Code == "CA" && r.DisplayName == "Canada");
        Assert.Contains(regions, r => r.Code == "UK" && r.DisplayName == "United Kingdom");
        Assert.Contains(regions, r => r.Code == "NL" && r.DisplayName == "Netherlands");

        // 2. Default Proton Options
        var options = new ProtonOptions();
        Assert.Equal("Windows", options.Platform);
        Assert.Equal("Block malware only", options.NetShield);
        Assert.False(options.ModerateNat);
        Assert.False(options.NatPmp);
        Assert.True(options.VpnAccelerator);
    }

    [Fact]
    public void CollectorViewModel_DefaultsToProtonVpn_AndSetsProtonPaths()
    {
        var storage = new ProfileStorageService(_inventoryPath);
        var vm = new CollectorViewModel(storageService: storage);

        Assert.Equal(ProviderConstants.ProtonVpn, vm.SelectedProvider);
        Assert.True(vm.IsProtonSelected);
        Assert.False(vm.IsVpnBookSelected);
        Assert.NotNull(vm.SelectedRegion);
        Assert.Equal("US", vm.SelectedRegion.Code);

        // Path should include 'Proton' folder
        Assert.Contains("Proton", vm.OutputFolder);
        Assert.Contains("US", vm.OutputFolder);
    }

    [Fact]
    public void ProfileStorageService_OrganizesProtonConfigs_InProtonSubdirectory()
    {
        var usProtonFolder = ProfileStorageService.GetRegionFolder(_configsRoot, "US", ProviderConstants.ProtonVpn);
        var caProtonFolder = ProfileStorageService.GetRegionFolder(_configsRoot, "CA", ProviderConstants.ProtonVpn);

        Assert.Equal(Path.Combine(_configsRoot, "Proton", "US"), usProtonFolder);
        Assert.Equal(Path.Combine(_configsRoot, "Proton", "CA"), caProtonFolder);
        Assert.True(Directory.Exists(usProtonFolder));
        Assert.True(Directory.Exists(caProtonFolder));
    }

    [Fact]
    public void SaveValidatedProfile_ValidProtonProfile_GeneratesExitCode0_AndRecordsInventory()
    {
        var storage = new ProfileStorageService(_inventoryPath);
        var usFolder = ProfileStorageService.GetRegionFolder(_configsRoot, "US", ProviderConstants.ProtonVpn);

        var sampleProtonConf = """
            # Proton VPN WireGuard Profile US-FREE#123
            [Interface]
            PrivateKey = aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa=
            Address = 10.2.0.2/32, 2a07:b944::2:2/128
            DNS = 10.2.0.1, 2a07:b944::2:1

            [Peer]
            PublicKey = bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb=
            AllowedIPs = 0.0.0.0/0, ::/0
            Endpoint = 185.159.158.1:51820
            PersistentKeepalive = 25
            """;

        var result = storage.SaveValidatedProfile(
            sampleProtonConf,
            usFolder,
            "US",
            "US-FREE#123",
            expiresAtUtc: null,
            provider: ProviderConstants.ProtonVpn);

        Assert.True(result.Success, $"Expected save to succeed but got: {result.Message}");
        Assert.Equal("us-001.conf", result.Filename);
        Assert.True(File.Exists(result.FinalFilePath));
        Assert.False(result.IsDuplicate);

        // Verify inventory
        var inventory = storage.LoadInventory();
        Assert.Single(inventory.Items);
        var item = inventory.Items[0];
        Assert.Equal("us-001.conf", item.Filename);
        Assert.Equal(ProviderConstants.ProtonVpn, item.Provider);
        Assert.Equal("US", item.Region);
        Assert.Equal("US-FREE#123", item.Server);
        Assert.Equal(result.IdentityHash, item.DerivedPublicIdentityHash);
        Assert.Equal("Valid", item.ValidationStatus);
        Assert.Null(item.ExpiresAtUtc);
    }

    [Fact]
    public void DuplicateDetection_QuarantinesDuplicatePublicIdentity_WithoutLoggingPrivateKey()
    {
        var storage = new ProfileStorageService(_inventoryPath);
        var usFolder = ProfileStorageService.GetRegionFolder(_configsRoot, "US", ProviderConstants.ProtonVpn);

        var profile1 = """
            [Interface]
            PrivateKey = aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa=
            Address = 10.2.0.2/32, 2a07:b944::2:2/128
            DNS = 10.2.0.1

            [Peer]
            PublicKey = bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb=
            AllowedIPs = 0.0.0.0/0, ::/0
            Endpoint = 185.159.158.1:51820
            """;

        // Same public identity (PublicKey, Address, Endpoint), different PrivateKey
        var profile2WithDifferentPrivKey = """
            [Interface]
            PrivateKey = ccccccccccccccccccccccccccccccccccccccccccc=
            Address = 10.2.0.2/32, 2a07:b944::2:2/128
            DNS = 10.2.0.1

            [Peer]
            PublicKey = bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb=
            AllowedIPs = 0.0.0.0/0, ::/0
            Endpoint = 185.159.158.1:51820
            """;

        var res1 = storage.SaveValidatedProfile(profile1, usFolder, "US", "Server 1");
        Assert.True(res1.Success);

        var res2 = storage.SaveValidatedProfile(profile2WithDifferentPrivKey, usFolder, "US", "Server 2");
        Assert.False(res2.Success);
        Assert.True(res2.IsDuplicate);
        Assert.Contains("Duplicate client identity detected", res2.Message);

        // Ensure private keys are never stored in inventory JSON
        var inventoryJson = File.ReadAllText(_inventoryPath);
        Assert.DoesNotContain("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa=", inventoryJson);
        Assert.DoesNotContain("ccccccccccccccccccccccccccccccccccccccccccc=", inventoryJson);
    }

    [Fact]
    public void SequentialNumbering_AndBatchResume_WorksCorrectly()
    {
        var storage = new ProfileStorageService(_inventoryPath);
        var usFolder = ProfileStorageService.GetRegionFolder(_configsRoot, "US", ProviderConstants.ProtonVpn);

        // Pre-create 3 files: us-001.conf, us-002.conf, us-003.conf
        File.WriteAllText(Path.Combine(usFolder, "us-001.conf"), "test");
        File.WriteAllText(Path.Combine(usFolder, "us-002.conf"), "test");
        File.WriteAllText(Path.Combine(usFolder, "us-003.conf"), "test");

        var count = ProfileStorageService.GetExistingConfigCount(usFolder, "US");
        Assert.Equal(3, count);

        var nextNum = ProfileStorageService.GetNextConfigNumber(usFolder, "US");
        Assert.Equal(4, nextNum);

        var vm = new CollectorViewModel(storageService: storage)
        {
            SelectedProvider = ProviderConstants.ProtonVpn,
            Quantity = 5,
            OutputFolder = usFolder
        };

        vm.CheckExistingConfigs();
        Assert.True(vm.CanResume);
        Assert.Contains("Found 3 existing profiles", vm.ResumeRecommendationText);
        Assert.Contains("resume remaining 2", vm.ResumeRecommendationText);

        vm.ResumeRemaining();
        Assert.Equal(2, vm.Quantity);
    }
}
