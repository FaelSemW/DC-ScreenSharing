using System.IO;
using System.Text.RegularExpressions;
using DCSS.ProfileCollector.Models;
using Microsoft.Playwright;

namespace DCSS.ProfileCollector.Services;

public class ProtonVpnAutomationService : IAsyncDisposable
{
    public const string AccountUrl = "https://account.protonvpn.com/";
    public const string DownloadsWireGuardUrl = "https://account.protonvpn.com/downloads#wireguard-configuration";
    public const string FallbackWireGuardUrl = "https://account.proton.me/u/0/vpn/wireguard";

    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private IPage? _page;
    private bool _isInitialized;
    private readonly string _userDataDir;
    private readonly object _lock = new();

    public string UserDataDir => _userDataDir;

    public ProtonVpnAutomationService(string? customUserDataDir = null)
    {
        if (!string.IsNullOrWhiteSpace(customUserDataDir))
        {
            _userDataDir = customUserDataDir;
        }
        else
        {
            var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DCSS.ProfileCollector", "browser_profile");
            Directory.CreateDirectory(appData);
            _userDataDir = appData;
        }
    }

    public static List<CollectorRegion> GetDefaultRegions()
    {
        return new List<CollectorRegion>
        {
            new()
            {
                Code = "US",
                DisplayName = "United States",
                Servers = new()
                {
                    new() { Id = "auto", Name = "Automatic / Recommended", CountryCode = "US", CountryName = "United States" },
                    new() { Id = "us-free-01", Name = "US Free #1", CountryCode = "US", CountryName = "United States" },
                    new() { Id = "us-free-02", Name = "US Free #2", CountryCode = "US", CountryName = "United States" }
                }
            },
            new()
            {
                Code = "CA",
                DisplayName = "Canada",
                Servers = new()
                {
                    new() { Id = "auto", Name = "Automatic / Recommended", CountryCode = "CA", CountryName = "Canada" },
                    new() { Id = "ca-free-01", Name = "Canada Free #1", CountryCode = "CA", CountryName = "Canada" }
                }
            },
            new()
            {
                Code = "UK",
                DisplayName = "United Kingdom",
                Servers = new()
                {
                    new() { Id = "auto", Name = "Automatic / Recommended", CountryCode = "UK", CountryName = "United Kingdom" },
                    new() { Id = "uk-free-01", Name = "UK Free #1", CountryCode = "GB", CountryName = "United Kingdom" }
                }
            },
            new()
            {
                Code = "NL",
                DisplayName = "Netherlands",
                Servers = new()
                {
                    new() { Id = "auto", Name = "Automatic / Recommended", CountryCode = "NL", CountryName = "Netherlands" },
                    new() { Id = "nl-free-01", Name = "NL Free #1", CountryCode = "NL", CountryName = "Netherlands" }
                }
            },
            new()
            {
                Code = "DE",
                DisplayName = "Germany",
                Servers = new()
                {
                    new() { Id = "auto", Name = "Automatic / Recommended", CountryCode = "DE", CountryName = "Germany" },
                    new() { Id = "de-free-01", Name = "DE Free #1", CountryCode = "DE", CountryName = "Germany" }
                }
            },
            new()
            {
                Code = "FR",
                DisplayName = "France",
                Servers = new()
                {
                    new() { Id = "auto", Name = "Automatic / Recommended", CountryCode = "FR", CountryName = "France" },
                    new() { Id = "fr-free-01", Name = "FR Free #1", CountryCode = "FR", CountryName = "France" }
                }
            },
            new()
            {
                Code = "JP",
                DisplayName = "Japan",
                Servers = new()
                {
                    new() { Id = "auto", Name = "Automatic / Recommended", CountryCode = "JP", CountryName = "Japan" },
                    new() { Id = "jp-free-01", Name = "JP Free #1", CountryCode = "JP", CountryName = "Japan" }
                }
            }
        };
    }

    public async Task InitializeBrowserAsync(bool headless = false)
    {
        if (_isInitialized && _context != null && _page != null && !_page.IsClosed)
        {
            return;
        }

        _playwright = await Playwright.CreateAsync();

        Directory.CreateDirectory(_userDataDir);

        // Auto-detect installed browser channels with graceful fallback
        var channelsToTry = new[] { "msedge", "chrome", null };

        foreach (var channel in channelsToTry)
        {
            try
            {
                var options = new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless = headless,
                    Channel = channel,
                    SlowMo = 100,
                    AcceptDownloads = true,
                    ViewportSize = new ViewportSize { Width = 1280, Height = 800 }
                };

                _context = await _playwright.Chromium.LaunchPersistentContextAsync(_userDataDir, options);
                _page = _context.Pages.Count > 0 ? _context.Pages[0] : await _context.NewPageAsync();
                _isInitialized = true;
                return;
            }
            catch
            {
                // Try next channel
            }
        }

        throw new InvalidOperationException("Could not launch visible browser (Edge/Chrome/Chromium). Please ensure Microsoft Edge or Google Chrome is installed.");
    }

    public async Task<bool> IsAuthenticatedAsync()
    {
        if (_page == null) return false;

        try
        {
            var url = _page.Url;
            if (string.IsNullOrEmpty(url) || url == "about:blank") return false;

            if (url.Contains("/login", StringComparison.OrdinalIgnoreCase)) return false;

            // Check if password field is visible
            var pwdField = await _page.QuerySelectorAsync("input[type=\"password\"]");
            if (pwdField != null && await pwdField.IsVisibleAsync()) return false;

            // Check if dashboard/downloads elements exist
            var authenticatedElement = await _page.QuerySelectorAsync("a[href*=\"downloads\"], button[data-testid*=\"user-dropdown\"], nav, main");
            return authenticatedElement != null;
        }
        catch
        {
            return false;
        }
    }

    public async Task NavigateToWireGuardPageAsync(Action<string>? statusCallback = null, CancellationToken ct = default)
    {
        if (_page == null) await InitializeBrowserAsync(headless: false);

        statusCallback?.Invoke("Navigating to Proton VPN WireGuard downloads...");

        try
        {
            await _page!.GotoAsync(DownloadsWireGuardUrl, new PageGotoOptions { Timeout = 30000, WaitUntil = WaitUntilState.DOMContentLoaded });
        }
        catch
        {
            // Try fallback url
            try
            {
                await _page!.GotoAsync(FallbackWireGuardUrl, new PageGotoOptions { Timeout = 30000, WaitUntil = WaitUntilState.DOMContentLoaded });
            }
            catch { }
        }

        if (_page != null)
        {
            await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        }
    }

    public async Task<ProviderProfileResult> GenerateSingleProfileAsync(
        ProfileGenerationOptions options,
        Action<string>? statusCallback = null,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (ct.IsCancellationRequested)
            {
                return new ProviderProfileResult { Success = false, ErrorMessage = "Batch cancelled by operator." };
            }
        }

        try
        {
            await InitializeBrowserAsync(headless: false);

            if (_page == null)
            {
                return new ProviderProfileResult { Success = false, ErrorMessage = "Failed to initialize browser session." };
            }

            statusCallback?.Invoke("Checking Proton VPN authentication status...");
            await NavigateToWireGuardPageAsync(statusCallback, ct);

            // Check if login is required
            var isAuth = await IsAuthenticatedAsync();
            if (!isAuth)
            {
                statusCallback?.Invoke("Sign in to Proton VPN in the browser, then click Continue.");
                return new ProviderProfileResult
                {
                    Success = false,
                    RequiresOperatorAttention = true,
                    OperatorAttentionReason = "Sign in to Proton VPN in the browser, then click Continue.",
                    ErrorMessage = "Sign in to Proton VPN in the browser, then click Continue."
                };
            }

            // Check for security challenges or rate limits
            var bodyText = await _page.InnerTextAsync("body");
            if (bodyText.Contains("security challenge", StringComparison.OrdinalIgnoreCase) ||
                bodyText.Contains("unusual activity", StringComparison.OrdinalIgnoreCase) ||
                bodyText.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
                bodyText.Contains("account restricted", StringComparison.OrdinalIgnoreCase))
            {
                statusCallback?.Invoke("Proton VPN requires operator attention.");
                return new ProviderProfileResult
                {
                    Success = false,
                    RequiresOperatorAttention = true,
                    OperatorAttentionReason = "Proton VPN requires operator attention.",
                    ErrorMessage = "Proton VPN requires operator attention."
                };
            }

            statusCallback?.Invoke($"Configuring WireGuard profile '{options.ConfigurationName}'...");

            // 1. Fill Configuration Name
            var nameInput = await _page.QuerySelectorAsync("input[name=\"name\"], input[placeholder*=\"name\" i], input[data-testid*=\"name\" i], input#name");
            if (nameInput != null && await nameInput.IsVisibleAsync())
            {
                await nameInput.FillAsync(options.ConfigurationName);
            }

            // 2. Platform Selection (Windows)
            var platformSelector = await _page.QuerySelectorAsync($"button:has-text(\"{options.ProtonSettings.Platform}\"), label:has-text(\"{options.ProtonSettings.Platform}\"), [data-testid*=\"{options.ProtonSettings.Platform.ToLowerInvariant()}\"]");
            if (platformSelector != null && await platformSelector.IsVisibleAsync())
            {
                try { await platformSelector.ClickAsync(); } catch { }
            }

            // 3. Country / Region selection
            var regionName = options.Region?.DisplayName ?? "United States";
            var countryDropdown = await _page.QuerySelectorAsync("select[name=\"country\"], [data-testid*=\"country\"], button:has-text(\"Country\"), div[class*=\"select\"]:has-text(\"Country\")");
            if (countryDropdown != null)
            {
                try
                {
                    await countryDropdown.ClickAsync();
                    var countryOption = await _page.QuerySelectorAsync($"option:has-text(\"{regionName}\"), [role=\"option\"]:has-text(\"{regionName}\"), li:has-text(\"{regionName}\")");
                    if (countryOption != null)
                    {
                        await countryOption.ClickAsync();
                    }
                }
                catch { }
            }

            // 4. VPN Accelerator (Default ON)
            if (options.ProtonSettings.VpnAccelerator)
            {
                var acceleratorCheckbox = await _page.QuerySelectorAsync("input[type=\"checkbox\"][name*=\"accelerator\" i], [data-testid*=\"accelerator\" i]");
                if (acceleratorCheckbox != null)
                {
                    try
                    {
                        var isChecked = await acceleratorCheckbox.IsCheckedAsync();
                        if (!isChecked) await acceleratorCheckbox.CheckAsync();
                    }
                    catch { }
                }
            }

            // 5. Moderate NAT (Default OFF)
            var moderateNatCheckbox = await _page.QuerySelectorAsync("input[type=\"checkbox\"][name*=\"moderate\" i], [data-testid*=\"moderate\" i]");
            if (moderateNatCheckbox != null)
            {
                try
                {
                    var isChecked = await moderateNatCheckbox.IsCheckedAsync();
                    if (isChecked && !options.ProtonSettings.ModerateNat) await moderateNatCheckbox.UncheckAsync();
                }
                catch { }
            }

            // 6. NAT-PMP (Default OFF)
            var natPmpCheckbox = await _page.QuerySelectorAsync("input[type=\"checkbox\"][name*=\"nat-pmp\" i], [data-testid*=\"pmp\" i]");
            if (natPmpCheckbox != null)
            {
                try
                {
                    var isChecked = await natPmpCheckbox.IsCheckedAsync();
                    if (isChecked && !options.ProtonSettings.NatPmp) await natPmpCheckbox.UncheckAsync();
                }
                catch { }
            }

            statusCallback?.Invoke($"Generating configuration for {regionName}...");

            // 7. Click Create / Generate button and wait for download
            var createBtn = await _page.QuerySelectorAsync("button:has-text(\"Create\"), button:has-text(\"Generate\"), button[type=\"submit\"]:has-text(\"Create\"), button[data-testid*=\"create\"]");

            IDownload? download = null;

            if (createBtn != null)
            {
                try
                {
                    // Start waiting for download before clicking
                    var downloadTask = _page.WaitForDownloadAsync(new PageWaitForDownloadOptions { Timeout = 25000 });
                    await createBtn.ClickAsync();

                    // Check if Download button appears after Create
                    await Task.Delay(1500, ct);
                    var downloadBtn = await _page.QuerySelectorAsync("button:has-text(\"Download\"), a:has-text(\"Download\"), button[data-testid*=\"download\"]");
                    if (downloadBtn != null && await downloadBtn.IsVisibleAsync())
                    {
                        await downloadBtn.ClickAsync();
                    }

                    download = await downloadTask;
                }
                catch (TimeoutException)
                {
                    // Check if a direct download link exists
                    var downloadLink = await _page.QuerySelectorAsync("a[download], a[href*=\".conf\"], button:has-text(\"Download\")");
                    if (downloadLink != null)
                    {
                        var downloadTask = _page.WaitForDownloadAsync(new PageWaitForDownloadOptions { Timeout = 15000 });
                        await downloadLink.ClickAsync();
                        download = await downloadTask;
                    }
                }
            }

            if (download != null)
            {
                var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.conf");
                await download.SaveAsAsync(tempPath);

                var content = await File.ReadAllTextAsync(tempPath, ct);
                try { File.Delete(tempPath); } catch { }

                if (!string.IsNullOrWhiteSpace(content))
                {
                    // Extract safe server name from download or config
                    var serverDisplayName = $"{regionName} ({options.ConfigurationName})";
                    var serverMatch = Regex.Match(content, @"#\s*(.+)", RegexOptions.Multiline);
                    if (serverMatch.Success && !string.IsNullOrWhiteSpace(serverMatch.Groups[1].Value))
                    {
                        serverDisplayName = serverMatch.Groups[1].Value.Trim();
                    }

                    statusCallback?.Invoke($"Successfully generated and received {serverDisplayName}.");
                    return new ProviderProfileResult
                    {
                        Success = true,
                        ConfigContent = content,
                        ServerName = serverDisplayName,
                        ExpiresAtUtc = null // Standard Proton configs do not have an artificial 7-day expiration
                    };
                }
            }

            return new ProviderProfileResult
            {
                Success = false,
                ErrorMessage = "Timeout waiting for WireGuard configuration download from Proton VPN."
            };
        }
        catch (OperationCanceledException)
        {
            return new ProviderProfileResult { Success = false, ErrorMessage = "Generation cancelled by operator." };
        }
        catch (Exception ex)
        {
            return new ProviderProfileResult
            {
                Success = false,
                ErrorMessage = $"Proton VPN automation error: {ex.Message}"
            };
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_page != null && !_page.IsClosed)
            {
                await _page.CloseAsync();
            }
            if (_context != null)
            {
                await _context.CloseAsync();
                await _context.DisposeAsync();
            }
            _playwright?.Dispose();
        }
        catch { }
        finally
        {
            _page = null;
            _context = null;
            _playwright = null;
            _isInitialized = false;
        }
    }
}
