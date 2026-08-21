using System.IO;
using System.Text.Json;
using DCScreenSharing.Core.Updates;
using DCScreenSharing.Shared.Logging;
using Xunit;

namespace DC_ScreenSharing.IntegrationTests;

public class ApplicationUpdateServiceTests
{
    private class TestLogger : IAppLogger
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warning(string message, Exception? ex = null) { }
        public void Error(string message, Exception? ex = null) { }
        public IReadOnlyList<string> GetRecentLogs(int count = 50) => Array.Empty<string>();
    }

    [Theory]
    [InlineData("1.0.1", "1.0.2", true)]
    [InlineData("1.0.9", "1.0.10", true)]
    [InlineData("1.9.0", "1.10.0", true)]
    [InlineData("1.0.2", "1.0.2", false)]
    [InlineData("1.0.3", "1.0.2", false)]
    [InlineData("2.0.0", "1.9.9", false)]
    public void VersionComparison_EvaluatesCorrectly(string currentStr, string remoteStr, bool expectedNewer)
    {
        var current = Version.Parse(currentStr);
        var remote = Version.Parse(remoteStr);

        var isNewer = remote > current;
        Assert.Equal(expectedNewer, isNewer);
    }

    [Fact]
    public void Sha256_ComputationAndVerification_PassesForValidData()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "DCSS_Test_Sha_" + Guid.NewGuid().ToString("N") + ".bin");
        var testBytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        File.WriteAllBytes(tempFile, testBytes);

        try
        {
            var hash = ApplicationUpdateService.ComputeSha256(tempFile);
            Assert.NotNull(hash);
            Assert.Equal(64, hash.Length);

            // Recomputing should be identical
            var hash2 = ApplicationUpdateService.ComputeSha256(tempFile);
            Assert.Equal(hash, hash2);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }

    [Fact]
    public void ReleaseAssetSelection_FiltersCorrectInstaller()
    {
        var json = @"{
            ""tag_name"": ""v1.0.2"",
            ""name"": ""DC-ScreenSharing v1.0.2"",
            ""draft"": false,
            ""prerelease"": false,
            ""assets"": [
                {
                    ""name"": ""DCSS.Maintainer.exe"",
                    ""browser_download_url"": ""https://github.com/download/Maintainer.exe"",
                    ""size"": 10000000
                },
                {
                    ""name"": ""DC-ScreenSharing-Setup-1.0.2.exe"",
                    ""browser_download_url"": ""https://github.com/download/DC-ScreenSharing-Setup-1.0.2.exe"",
                    ""size"": 60000000
                },
                {
                    ""name"": ""DC-ScreenSharing-Setup-1.0.2.exe.sha256"",
                    ""browser_download_url"": ""https://github.com/download/DC-ScreenSharing-Setup-1.0.2.exe.sha256"",
                    ""size"": 64
                }
            ]
        }";

        var release = JsonSerializer.Deserialize<GitHubReleaseResponse>(json);
        Assert.NotNull(release);

        var installerAsset = release.Assets.FirstOrDefault(a => 
            a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && 
            !a.Name.Contains("Maintainer", StringComparison.OrdinalIgnoreCase) &&
            !a.Name.Contains("Collector", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(installerAsset);
        Assert.Equal("DC-ScreenSharing-Setup-1.0.2.exe", installerAsset.Name);

        var checksumAsset = release.Assets.FirstOrDefault(a => 
            a.Name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) || 
            a.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(checksumAsset);
        Assert.Equal("DC-ScreenSharing-Setup-1.0.2.exe.sha256", checksumAsset.Name);
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _responseJson;

        public MockHttpMessageHandler(string responseJson)
        {
            _responseJson = responseJson;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(_responseJson, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task CheckForUpdatesAsync_FromOlderVersion_DetectsLatestRelease()
    {
        var mockReleaseJson = @"{
            ""tag_name"": ""v1.0.10"",
            ""name"": ""DC-ScreenSharing v1.0.10"",
            ""draft"": false,
            ""prerelease"": false,
            ""body"": ""Release notes for v1.0.10"",
            ""assets"": [
                {
                    ""name"": ""DC-ScreenSharing-Setup-1.0.10.exe"",
                    ""browser_download_url"": ""https://github.com/FaelSemW/DC-ScreenSharing/releases/download/v1.0.10/DC-ScreenSharing-Setup-1.0.10.exe"",
                    ""size"": 63995647
                },
                {
                    ""name"": ""DC-ScreenSharing-Setup-1.0.10.exe.sha256"",
                    ""browser_download_url"": ""https://github.com/FaelSemW/DC-ScreenSharing/releases/download/v1.0.10/DC-ScreenSharing-Setup-1.0.10.exe.sha256"",
                    ""size"": 64
                }
            ]
        }";

        var mockClient = new HttpClient(new MockHttpMessageHandler(mockReleaseJson));
        var updateService = new ApplicationUpdateService(new TestLogger(), httpClient: mockClient);

        var result = await updateService.CheckForUpdatesAsync(new Version(1, 0, 8));

        Assert.True(result.UpdateAvailable);
        Assert.Equal(new Version(1, 0, 10), result.LatestVersion);
        Assert.NotNull(result.DownloadUrl);
        Assert.Contains("DC-ScreenSharing-Setup-1.0.10.exe", result.DownloadUrl);

        // When running a future or matching latest version, no update should be available
        var currentResult = await updateService.CheckForUpdatesAsync(new Version(2, 0, 0));
        Assert.False(currentResult.UpdateAvailable);
    }
}
