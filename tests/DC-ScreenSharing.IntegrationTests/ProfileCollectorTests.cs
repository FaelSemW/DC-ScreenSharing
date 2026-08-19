using System.IO;
using System.Text.Json;
using DCSS.ProfileCollector.Models;
using DCSS.ProfileCollector.Services;
using DCSS.ProfileCollector.ViewModels;
using Xunit;

namespace DC_ScreenSharing.IntegrationTests;

public class ProfileCollectorTests
{
    private const string ValidSyntheticConfig = @"[Interface]
PrivateKey = aGVsbG93b3JsZHByaXZhdGVrZXkxMjM0NTY3ODkwMTI=
Address = 10.8.0.2/32
DNS = 1.1.1.1, 8.8.8.8
MTU = 1420

[Peer]
PublicKey = c2VydmVycHVibGlja2V5MTIzNDU2Nzg5MDEyMzQ1Njc4OTA=
Endpoint = 192.0.2.1:443
AllowedIPs = 0.0.0.0/0, ::/0
";

    private const string ValidSyntheticConfig2 = @"[Interface]
PrivateKey = YW5vdGhlcnByaXZhdGVrZXkxMjM0NTY3ODkwMTIzNDU2Nzg=
Address = 10.8.0.3/32
DNS = 1.1.1.1
MTU = 1420

[Peer]
PublicKey = c2VydmVycHVibGlja2V5MTIzNDU2Nzg5MDEyMzQ1Njc4OTA=
Endpoint = 192.0.2.2:443
AllowedIPs = 0.0.0.0/0, ::/0
";

    [Fact]
    public void AutomaticNumberContinuation_AndZeroPadding_CalculatesCorrectly()
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), "DCSS_Test_Continuation_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempFolder);

        try
        {
            // Empty folder should start at 1
            Assert.Equal(1, ProfileStorageService.GetNextConfigNumber(tempFolder, "US"));
            Assert.Equal("us-001.conf", ProfileStorageService.FormatConfigFileName("US", 1));

            // Create existing files us-001.conf through us-027.conf
            for (int i = 1; i <= 27; i++)
            {
                File.WriteAllText(Path.Combine(tempFolder, $"us-{i:D3}.conf"), "dummy");
            }

            // Next should be 28 -> us-028.conf
            Assert.Equal(28, ProfileStorageService.GetNextConfigNumber(tempFolder, "US"));
            Assert.Equal("us-028.conf", ProfileStorageService.FormatConfigFileName("US", 28));

            // Check existing count
            Assert.Equal(27, ProfileStorageService.GetExistingConfigCount(tempFolder, "US"));
        }
        finally
        {
            if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true);
        }
    }

    [Fact]
    public void WireGuardValidation_ValidConfig_Passes()
    {
        var (isValid, parsed, errorMsg) = ProfileStorageService.ValidateConfigContent(ValidSyntheticConfig);
        Assert.True(isValid);
        Assert.NotNull(parsed);
        Assert.Equal("aGVsbG93b3JsZHByaXZhdGVrZXkxMjM0NTY3ODkwMTI=", parsed.PrivateKey);
        Assert.Equal("10.8.0.2/32", parsed.Address);
        Assert.Equal("c2VydmVycHVibGlja2V5MTIzNDU2Nzg5MDEyMzQ1Njc4OTA=", parsed.PeerPublicKey);
        Assert.Equal("192.0.2.1", parsed.Endpoint);
        Assert.Equal(443, parsed.Port);
        Assert.Empty(errorMsg);
    }

    [Fact]
    public void WireGuardValidation_HtmlErrorPage_Rejected()
    {
        var htmlError = "<!DOCTYPE html><html><body><h1>Rate Limit Exceeded</h1></body></html>";
        var (isValid, parsed, errorMsg) = ProfileStorageService.ValidateConfigContent(htmlError);
        Assert.False(isValid);
        Assert.Null(parsed);
        Assert.Contains("HTML error page", errorMsg);
    }

    [Fact]
    public void WireGuardValidation_MalformedOrEmpty_Rejected()
    {
        var (isValid1, _, errorMsg1) = ProfileStorageService.ValidateConfigContent("");
        Assert.False(isValid1);
        Assert.Contains("empty", errorMsg1);

        var (isValid2, _, errorMsg2) = ProfileStorageService.ValidateConfigContent("[Interface]\nPrivateKey=\n");
        Assert.False(isValid2);
        Assert.Contains("PrivateKey", errorMsg2);
    }

    [Fact]
    public void DuplicateIdentityDetection_QuarantinesAndRejects()
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), "DCSS_Test_Dup_" + Guid.NewGuid().ToString("N"));
        var customInventory = Path.Combine(tempFolder, "inventory.json");
        Directory.CreateDirectory(tempFolder);

        try
        {
            var storage = new ProfileStorageService(customInventory);

            // Save first profile
            var res1 = storage.SaveValidatedProfile(ValidSyntheticConfig, tempFolder, "US", "US Server 1");
            Assert.True(res1.Success);
            Assert.False(res1.IsDuplicate);
            Assert.Equal("us-001.conf", res1.Filename);
            Assert.True(File.Exists(res1.FinalFilePath));

            // Attempt saving exact duplicate identity (even if whitespace / comments differ)
            var duplicateContent = "# A duplicate with comment\n" + ValidSyntheticConfig;
            var res2 = storage.SaveValidatedProfile(duplicateContent, tempFolder, "US", "US Server 1");
            Assert.False(res2.Success);
            Assert.True(res2.IsDuplicate);
            Assert.Contains("Duplicate", res2.Message);

            // Second unique profile should succeed
            var res3 = storage.SaveValidatedProfile(ValidSyntheticConfig2, tempFolder, "US", "US Server 2");
            Assert.True(res3.Success);
            Assert.False(res3.IsDuplicate);
            Assert.Equal("us-002.conf", res3.Filename);
        }
        finally
        {
            if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true);
        }
    }

    [Fact]
    public void InventoryDatabase_SafeExclusionOfPrivateKeys()
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), "DCSS_Test_Inv_" + Guid.NewGuid().ToString("N"));
        var customInventory = Path.Combine(tempFolder, "inventory.json");
        Directory.CreateDirectory(tempFolder);

        try
        {
            var storage = new ProfileStorageService(customInventory);
            storage.SaveValidatedProfile(ValidSyntheticConfig, tempFolder, "US", "US Server 1");

            Assert.True(File.Exists(customInventory));
            var inventoryText = File.ReadAllText(customInventory);

            // Assert private key is NEVER in inventory file
            Assert.DoesNotContain("aGVsbG93b3JsZHByaXZhdGVrZXkxMjM0NTY3ODkwMTI=", inventoryText);
            Assert.DoesNotContain("PrivateKey", inventoryText);

            // Assert public identity hash is stored
            var db = JsonSerializer.Deserialize<InventoryDatabase>(inventoryText);
            Assert.NotNull(db);
            Assert.Single(db.Items);
            Assert.NotEmpty(db.Items[0].DerivedPublicIdentityHash);
            Assert.Equal("us-001.conf", db.Items[0].Filename);
            Assert.Equal("US", db.Items[0].Region);
        }
        finally
        {
            if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true);
        }
    }

    [Fact]
    public void SessionRecovery_ResumeCountCalculation()
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), "DCSS_Test_Resume_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempFolder);

        try
        {
            // Create 13 existing files
            for (int i = 1; i <= 13; i++)
            {
                File.WriteAllText(Path.Combine(tempFolder, $"us-{i:D3}.conf"), "dummy");
            }

            var vm = new CollectorViewModel();
            vm.OutputFolder = tempFolder;
            vm.Quantity = 20;

            vm.CheckExistingConfigs();
            Assert.True(vm.CanResume);
            Assert.Contains("Found 13 existing", vm.ResumeRecommendationText);
            Assert.Contains("resume remaining 7", vm.ResumeRecommendationText);

            vm.ResumeRemaining();
            Assert.Equal(7, vm.Quantity);
        }
        finally
        {
            if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true);
        }
    }

    [Fact]
    public void VpnBookRegion_DefaultStructures_AreValid()
    {
        var regions = VpnBookAutomationService.GetDefaultRegions();
        Assert.Equal(5, regions.Count);

        var codes = regions.Select(r => r.Code).ToList();
        Assert.Contains("US", codes);
        Assert.Contains("CA", codes);
        Assert.Contains("UK", codes);
        Assert.Contains("DE", codes);
        Assert.Contains("FR", codes);

        foreach (var region in regions)
        {
            Assert.NotEmpty(region.DisplayName);
            Assert.True(region.Servers.Count >= 2);
            foreach (var server in region.Servers)
            {
                Assert.NotEmpty(server.Id);
                Assert.NotEmpty(server.Hostname);
                Assert.Contains("vpnbook.com", server.Hostname);
            }
        }

        var ports = VpnBookAutomationService.GetDefaultPorts();
        Assert.Contains(ports, p => p.Port == "443");
        Assert.Contains(ports, p => p.Port == "80");
        Assert.Contains(ports, p => p.Port == "123");
        Assert.Contains(ports, p => p.Port == "25018");
    }
}
