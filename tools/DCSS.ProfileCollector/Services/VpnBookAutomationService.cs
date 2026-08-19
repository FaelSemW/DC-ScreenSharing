using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using DCSS.ProfileCollector.Models;

namespace DCSS.ProfileCollector.Services;

public class VpnBookAutomationService : IAsyncDisposable
{
    public const string TargetUrl = "https://www.vpnbook.com/freevpn/wireguard-vpn";

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;
    private bool _isInitialized;
    private readonly object _lock = new();

    public static List<VpnBookRegion> GetDefaultRegions()
    {
        return new List<VpnBookRegion>
        {
            new()
            {
                Code = "US",
                DisplayName = "United States",
                Servers = new()
                {
                    new() { Id = "us16", Name = "US Server 1", Hostname = "us16.vpnbook.com", CountryCode = "US", CountryName = "United States" },
                    new() { Id = "us178", Name = "US Server 2", Hostname = "us178.vpnbook.com", CountryCode = "US", CountryName = "United States" }
                }
            },
            new()
            {
                Code = "CA",
                DisplayName = "Canada",
                Servers = new()
                {
                    new() { Id = "ca149", Name = "Canada Server 1", Hostname = "ca149.vpnbook.com", CountryCode = "CA", CountryName = "Canada" },
                    new() { Id = "ca196", Name = "Canada Server 2", Hostname = "ca196.vpnbook.com", CountryCode = "CA", CountryName = "Canada" }
                }
            },
            new()
            {
                Code = "UK",
                DisplayName = "United Kingdom",
                Servers = new()
                {
                    new() { Id = "uk205", Name = "UK Server 1", Hostname = "uk205.vpnbook.com", CountryCode = "GB", CountryName = "United Kingdom" },
                    new() { Id = "uk68", Name = "UK Server 2", Hostname = "uk68.vpnbook.com", CountryCode = "GB", CountryName = "United Kingdom" }
                }
            },
            new()
            {
                Code = "DE",
                DisplayName = "Germany",
                Servers = new()
                {
                    new() { Id = "de20", Name = "Germany Server 1", Hostname = "de20.vpnbook.com", CountryCode = "DE", CountryName = "Germany" },
                    new() { Id = "de220", Name = "Germany Server 2", Hostname = "de220.vpnbook.com", CountryCode = "DE", CountryName = "Germany" }
                }
            },
            new()
            {
                Code = "FR",
                DisplayName = "France",
                Servers = new()
                {
                    new() { Id = "fr200", Name = "France Server 1", Hostname = "fr200.vpnbook.com", CountryCode = "FR", CountryName = "France" },
                    new() { Id = "fr231", Name = "France Server 2", Hostname = "fr2311.vpnbook.com", CountryCode = "FR", CountryName = "France" }
                }
            }
        };
    }

    public static List<PortOption> GetDefaultPorts()
    {
        return new List<PortOption>
        {
            new() { Port = "443", Description = "443 (HTTPS) - Best for bypassing firewalls" },
            new() { Port = "80", Description = "80 (HTTP) - Alternative if 443 is blocked" },
            new() { Port = "123", Description = "123 (NTP) - Good for restricted networks" },
            new() { Port = "25018", Description = "25018 - High port, best speeds" }
        };
    }

    public async Task InitializeBrowserAsync(bool headless = false)
    {
        if (_isInitialized && _browser != null && _page != null && !_page.IsClosed)
        {
            return;
        }

        _playwright = await Playwright.CreateAsync();

        var launchOptions = new BrowserTypeLaunchOptions
        {
            Headless = headless,
            SlowMo = 100
        };

        // Attempt Chrome, then Edge, then default Chromium
        try
        {
            launchOptions.Channel = "chrome";
            _browser = await _playwright.Chromium.LaunchAsync(launchOptions);
        }
        catch
        {
            try
            {
                launchOptions.Channel = "msedge";
                _browser = await _playwright.Chromium.LaunchAsync(launchOptions);
            }
            catch
            {
                launchOptions.Channel = null;
                _browser = await _playwright.Chromium.LaunchAsync(launchOptions);
            }
        }

        _context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36"
        });

        _page = await _context.NewPageAsync();
        _isInitialized = true;
    }

    public async Task<List<VpnBookRegion>> DiscoverAvailableRegionsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await InitializeBrowserAsync(headless: false);
            if (_page == null) return GetDefaultRegions();

            await _page.GotoAsync(TargetUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 15000 });

            // Extract dynamic servers from next.js page scripts if present
            var html = await _page.ContentAsync();
            var match = Regex.Match(html, @"\""servers\"":(\[[^\]]+\])");
            if (match.Success)
            {
                var serversJson = match.Groups[1].Value;
                var servers = JsonSerializer.Deserialize<List<VpnBookServer>>(serversJson);
                if (servers != null && servers.Count > 0)
                {
                    var regionGroups = servers.GroupBy(s => s.CountryCode.ToUpperInvariant()).ToList();
                    var regions = new List<VpnBookRegion>();

                    var codeToDisplay = new Dictionary<string, (string Code, string Name)>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "US", ("US", "United States") },
                        { "CA", ("CA", "Canada") },
                        { "GB", ("UK", "United Kingdom") },
                        { "UK", ("UK", "United Kingdom") },
                        { "DE", ("DE", "Germany") },
                        { "FR", ("FR", "France") }
                    };

                    foreach (var group in regionGroups)
                    {
                        var key = group.Key;
                        var (normCode, normName) = codeToDisplay.TryGetValue(key, out var mapping)
                            ? mapping
                            : (key, group.First().CountryName);

                        regions.Add(new VpnBookRegion
                        {
                            Code = normCode,
                            DisplayName = normName,
                            Servers = group.ToList()
                        });
                    }

                    if (regions.Count > 0)
                    {
                        return regions;
                    }
                }
            }
        }
        catch
        {
            // Fall back cleanly to robust default static structure
        }

        return GetDefaultRegions();
    }

    public async Task<(bool Success, string ConfigContent, string ServerName, DateTime? ExpiresAtUtc, string ErrorMessage, bool RequiresOperatorAttention)> GenerateSingleProfileAsync(
        VpnBookServer server,
        string port,
        Action<string>? statusCallback = null,
        CancellationToken cancellationToken = default)
    {
        const int maxRetries = 2;
        int attempt = 0;

        while (attempt <= maxRetries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;

            try
            {
                statusCallback?.Invoke($"Connecting to VPNBook generator (Attempt {attempt})...");
                await InitializeBrowserAsync(headless: false);
                if (_page == null) throw new InvalidOperationException("Browser page could not be initialized.");

                await _page.GotoAsync(TargetUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 25000 });

                // 1. Select Server Button
                statusCallback?.Invoke($"Selecting server: {server.Name} ({server.Hostname})...");
                var serverBtn = _page.Locator($"button:has-text('{server.Name}'), button:has-text('{server.Hostname}')").First;
                if (await serverBtn.CountAsync() > 0)
                {
                    await serverBtn.ClickAsync();
                }
                else
                {
                    // Fallback search
                    var fallbackBtn = _page.Locator($"button:has-text('{server.Id}')").First;
                    if (await fallbackBtn.CountAsync() > 0)
                    {
                        await fallbackBtn.ClickAsync();
                    }
                }

                await Task.Delay(500, cancellationToken);

                // 2. Select Port Button
                statusCallback?.Invoke($"Selecting port: {port}...");
                var portBtn = _page.Locator($"button:has-text('{port}')").First;
                if (await portBtn.CountAsync() > 0)
                {
                    await portBtn.ClickAsync();
                }

                await Task.Delay(500, cancellationToken);

                // 3. Confirm Conditions Checkbox
                statusCallback?.Invoke("Accepting provider conditions checkbox...");
                var checkbox = _page.Locator("input[type='checkbox']").First;
                if (await checkbox.CountAsync() > 0)
                {
                    var isChecked = await checkbox.IsCheckedAsync();
                    if (!isChecked)
                    {
                        await checkbox.CheckAsync();
                    }
                }

                await Task.Delay(800, cancellationToken);

                // 4. Locate Generate Configuration Button
                var generateBtn = _page.Locator("button:has-text('Generate Configuration')").First;
                if (await generateBtn.CountAsync() == 0)
                {
                    throw new InvalidOperationException("Generate Configuration button not found on page.");
                }

                // Check if button is disabled due to Turnstile verification
                bool isBtnDisabled = await generateBtn.IsDisabledAsync();
                if (isBtnDisabled)
                {
                    statusCallback?.Invoke("Generation paused. VPNBook requires operator attention (CAPTCHA/Challenge).");

                    // Wait up to 60 seconds for operator or automatic Turnstile resolution
                    var waitStart = DateTime.UtcNow;
                    while (isBtnDisabled && (DateTime.UtcNow - waitStart).TotalSeconds < 60)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await Task.Delay(1000, cancellationToken);
                        isBtnDisabled = await generateBtn.IsDisabledAsync();
                    }

                    if (isBtnDisabled)
                    {
                        return (false, string.Empty, server.Name, null, "Generation paused. VPNBook requires operator attention (Turnstile not completed).", true);
                    }
                }

                // Click Generate Configuration
                statusCallback?.Invoke("Submitting generation request to VPNBook...");
                await generateBtn.ClickAsync();

                // 5. Wait for result card or error message
                statusCallback?.Invoke("Waiting for WireGuard configuration generation...");

                var startTime = DateTime.UtcNow;
                string rawConfig = string.Empty;
                bool hasResult = false;
                bool isError = false;
                string errorMsg = string.Empty;

                while ((DateTime.UtcNow - startTime).TotalSeconds < 30)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Check for config in <pre> block
                    var pre = _page.Locator("pre").First;
                    if (await pre.CountAsync() > 0)
                    {
                        var text = await pre.InnerTextAsync();
                        if (!string.IsNullOrWhiteSpace(text) && text.Contains("[Interface]") && text.Contains("[Peer]"))
                        {
                            rawConfig = text;
                            hasResult = true;
                            break;
                        }
                    }

                    // Check for error card
                    var errorAlert = _page.Locator("div[class*='border-red'], div[class*='bg-red']").First;
                    if (await errorAlert.CountAsync() > 0)
                    {
                        errorMsg = await errorAlert.InnerTextAsync();
                        if (!string.IsNullOrWhiteSpace(errorMsg))
                        {
                            isError = true;
                            break;
                        }
                    }

                    await Task.Delay(1000, cancellationToken);
                }

                if (hasResult && !string.IsNullOrWhiteSpace(rawConfig))
                {
                    statusCallback?.Invoke("WireGuard configuration successfully received!");
                    return (true, rawConfig, server.Name, DateTime.UtcNow.AddDays(7), string.Empty, false);
                }

                if (isError)
                {
                    var isRateLimit = errorMsg.Contains("rate", StringComparison.OrdinalIgnoreCase) ||
                                      errorMsg.Contains("limit", StringComparison.OrdinalIgnoreCase) ||
                                      errorMsg.Contains("too many", StringComparison.OrdinalIgnoreCase) ||
                                      errorMsg.Contains("blocked", StringComparison.OrdinalIgnoreCase);

                    if (isRateLimit)
                    {
                        return (false, string.Empty, server.Name, null, $"Generation paused. VPNBook requires operator attention: {errorMsg}", true);
                    }

                    // Bounded retry on transient error
                    if (attempt <= maxRetries)
                    {
                        statusCallback?.Invoke($"Provider reported temporary error: {errorMsg}. Retrying in 4s...");
                        await Task.Delay(4000, cancellationToken);
                        continue;
                    }

                    return (false, string.Empty, server.Name, null, $"VPNBook generation error: {errorMsg}", false);
                }

                // If timed out waiting for result
                if (attempt <= maxRetries)
                {
                    statusCallback?.Invoke("Timeout waiting for config generation response. Retrying...");
                    await Task.Delay(3000, cancellationToken);
                    continue;
                }

                return (false, string.Empty, server.Name, null, "Timeout waiting for WireGuard configuration output from VPNBook.", false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt <= maxRetries)
                {
                    statusCallback?.Invoke($"Automation error: {ex.Message}. Retrying...");
                    await Task.Delay(3000, cancellationToken);
                    continue;
                }

                return (false, string.Empty, server.Name, null, $"Browser automation error: {ex.Message}", false);
            }
        }

        return (false, string.Empty, server.Name, null, "Failed to generate profile after maximum retries.", false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_page != null)
            {
                await _page.CloseAsync();
                _page = null;
            }

            if (_context != null)
            {
                await _context.CloseAsync();
                _context = null;
            }

            if (_browser != null)
            {
                await _browser.CloseAsync();
                _browser = null;
            }

            if (_playwright != null)
            {
                _playwright.Dispose();
                _playwright = null;
            }
        }
        catch { }
        finally
        {
            _isInitialized = false;
        }
    }
}
