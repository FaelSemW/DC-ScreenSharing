using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
using DC_ScreenSharing.Networking.ProcessIsolation;
using DCScreenSharing.Core.Profiles;
using DCScreenSharing.Shared;
using DCScreenSharing.Shared.Contracts;
using DCScreenSharing.Shared.Logging;

namespace DCScreenSharing.Networking;

public class ProcessRoutingEngine : IAsyncDisposable
{
    private readonly IAppLogger _logger;
    private readonly string _workingDirectory;
    private readonly WinDivertProcessIsolationEngine _isolationEngine;
    private Process? _engineProcess;
    private Process? _openVpnProcess;
    private string? _tempCredentialsPath;
    private string? _tempOvpnConfigPath;
    private readonly object _engineLock = new();

    public bool IsRunning
    {
        get
        {
            lock (_engineLock)
            {
                var isProcRunning = (_engineProcess != null && !_engineProcess.HasExited) ||
                                    (_openVpnProcess != null && !_openVpnProcess.HasExited);
                return isProcRunning || _isolationEngine.IsRunning;
            }
        }
    }

    public ProcessRoutingEngine(IAppLogger logger, string? workingDirectory = null)
    {
        _logger = logger;
        _workingDirectory = workingDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DC-ScreenSharing");
        _isolationEngine = new WinDivertProcessIsolationEngine();

        try
        {
            Directory.CreateDirectory(_workingDirectory);
        }
        catch { }
    }

    public (bool Success, string? ErrorCode, string Message) Start(TunnelConfiguration config)
    {
        lock (_engineLock)
        {
            if (IsRunning)
            {
                _logger.Warning("Routing engine is already running. Stopping previous instance first.");
                Stop();
            }

            try
            {
                var isOvpn = string.Equals(config.Protocol, VpnProtocol.OpenVpn, StringComparison.OrdinalIgnoreCase);
                if (isOvpn)
                {
                    return StartOpenVpn(config);
                }
                else
                {
                    return StartWireGuard(config);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("[RoutingEngine] Exception starting routing engine", ex);
                return (false, "InternalEngineError", $"Engine error: {ex.Message}");
            }
        }
    }

    // ======================================================================
    // OPENVPN ENGINE EXECUTION (PRIMARY TRANSPORT)
    // ======================================================================

    private (bool Success, string? ErrorCode, string Message) StartOpenVpn(TunnelConfiguration config)
    {
        _logger.Info($"[OpenVPN] Preparing OpenVPN connection to '{config.ServerName}' ({config.Endpoint}:{config.Port})...");

        var openVpnExe = FindOpenVpnExecutable();
        if (string.IsNullOrEmpty(openVpnExe) || !File.Exists(openVpnExe))
        {
            _logger.Error("[OpenVPN] Could not locate openvpn.exe runtime binary.");
            return (false, "OpenVpnNotFound", "Could not locate OpenVPN runtime binary (openvpn.exe).");
        }

        // 1. Parse OpenVPN runtime profile configuration
        OpenVpnProfileConfig? ovpnConfig = null;
        if (!string.IsNullOrEmpty(config.OpenVpnProfileJson))
        {
            try
            {
                ovpnConfig = JsonSerializer.Deserialize<OpenVpnProfileConfig>(config.OpenVpnProfileJson);
            }
            catch { }
        }

        // 2. Generate sanitized temporary .ovpn file
        _tempOvpnConfigPath = Path.Combine(_workingDirectory, $"openvpn_runtime_{Guid.NewGuid():N}.ovpn");
        var ovpnContent = GenerateSanitizedOvpnConfig(config, ovpnConfig);
        File.WriteAllText(_tempOvpnConfigPath, ovpnContent);

        // 3. Write temporary auth-user-pass credentials file if needed (ACL restricted)
        if (ovpnConfig != null && !string.IsNullOrEmpty(ovpnConfig.Username))
        {
            _tempCredentialsPath = Path.Combine(_workingDirectory, $"ovpn_creds_{Guid.NewGuid():N}.tmp");
            var credsText = $"{ovpnConfig.Username}\n{ovpnConfig.EncryptedPassword ?? ""}\n";
            File.WriteAllText(_tempCredentialsPath, credsText);
        }

        // 4. Build arguments
        var argsList = new List<string>
        {
            $"--config \"{_tempOvpnConfigPath}\"",
            "--windows-driver wintun",
            "--route-nopull",
            "--pull-filter ignore \"redirect-gateway\"",
            "--pull-filter ignore \"dhcp-option DNS\"",
            "--verb 3"
        };

        if (!string.IsNullOrEmpty(_tempCredentialsPath))
        {
            argsList.Add($"--auth-user-pass \"{_tempCredentialsPath}\"");
        }

        var args = string.Join(" ", argsList);
        _logger.Info($"[OpenVPN] Launching openvpn.exe with safe arguments: {Sanitizer.Sanitize(args)}");

        var psi = new ProcessStartInfo
        {
            FileName = openVpnExe,
            Arguments = args,
            WorkingDirectory = Path.GetDirectoryName(openVpnExe) ?? _workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        _openVpnProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var tcsReady = new TaskCompletionSource<bool>();

        _openVpnProcess.OutputDataReceived += (s, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _logger.Debug($"[OpenVPN Stdout] {Sanitizer.Sanitize(e.Data)}");
                if (e.Data.Contains("Initialization Sequence Completed", StringComparison.OrdinalIgnoreCase))
                {
                    tcsReady.TrySetResult(true);
                }
                else if (e.Data.Contains("AUTH_FAILED", StringComparison.OrdinalIgnoreCase))
                {
                    tcsReady.TrySetResult(false);
                }
            }
        };

        _openVpnProcess.ErrorDataReceived += (s, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _logger.Warning($"[OpenVPN Stderr] {Sanitizer.Sanitize(e.Data)}");
            }
        };

        if (!_openVpnProcess.Start())
        {
            CleanupTempFiles();
            return (false, "OpenVpnStartFailed", "Failed to start openvpn.exe process.");
        }

        _openVpnProcess.BeginOutputReadLine();
        _openVpnProcess.BeginErrorReadLine();

        // 5. Wait for readiness or exit
        bool isReady = false;
        try
        {
            var completedTask = Task.WhenAny(tcsReady.Task, Task.Delay(10000)).GetAwaiter().GetResult();
            isReady = completedTask == tcsReady.Task && tcsReady.Task.Result;
        }
        catch { }

        if (_openVpnProcess.HasExited)
        {
            int exitCode = _openVpnProcess.ExitCode;
            CleanupTempFiles();
            return (false, "OpenVpnExitedEarly", $"OpenVPN terminated with exit code {exitCode}.");
        }

        // 6. Discover VPN adapter name and start sing-box TUN process routing
        var vpnAdapter = InterfaceBindingService.FindVpnAdapter("OpenVPN");
        string adapterName = vpnAdapter?.Name ?? "OpenVPN";
        _logger.Info($"[OpenVPN] OpenVPN tunnel active on adapter '{adapterName}'. Starting process routing engine...");

        var engineExe = FindEngineExecutable();
        if (!string.IsNullOrEmpty(engineExe) && File.Exists(engineExe))
        {
            EnsureWintunDll(Path.GetDirectoryName(engineExe) ?? _workingDirectory);
            var configJson = GenerateEngineConfig(config, openVpnAdapterName: adapterName);
            var configPath = Path.Combine(_workingDirectory, "engine_config.json");
            File.WriteAllText(configPath, configJson);

            var enginePsi = new ProcessStartInfo
            {
                FileName = engineExe,
                Arguments = $"run -c \"{configPath}\"",
                WorkingDirectory = _workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            _engineProcess = new Process { StartInfo = enginePsi, EnableRaisingEvents = true };
            _engineProcess.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    _logger.Debug($"[Engine Stdout] {Sanitizer.Sanitize(e.Data)}");
                }
            };
            _engineProcess.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    _logger.Warning($"[Engine Stderr] {Sanitizer.Sanitize(e.Data)}");
                }
            };

            if (!_engineProcess.Start())
            {
                _logger.Warning("[OpenVPN] Failed to start secondary process routing engine; falling back to direct OpenVPN adapter.");
            }
            else
            {
                _engineProcess.BeginOutputReadLine();
                _engineProcess.BeginErrorReadLine();
            }
        }

        // Start WinDivert process isolation pointing target traffic to local proxy transport
        _isolationEngine.StartAsync(new ProcessIsolationOptions
        {
            TargetProcessNames = config.AllowedApps ?? new List<string> { "Discord.exe", "DiscordPTB.exe", "DiscordCanary.exe" },
            TransportType = "OpenVPN"
        }).GetAwaiter().GetResult();

        _logger.Info($"[OpenVPN] OpenVPN tunnel and WinDivert process isolation active.");
        return (true, null, "OpenVPN tunnel active.");
    }

    private string GenerateSanitizedOvpnConfig(TunnelConfiguration config, OpenVpnProfileConfig? ovpn)
    {
        var lines = new List<string>
        {
            "client",
            "dev tun",
            $"proto {((ovpn?.Protocol ?? "UDP").Equals("TCP", StringComparison.OrdinalIgnoreCase) ? "tcp-client" : "udp")}",
            $"remote {config.Endpoint} {config.Port}",
            "resolv-retry infinite",
            "nobind",
            "persist-key",
            "persist-tun",
            "route-nopull"
        };

        if (ovpn != null)
        {
            if (!string.IsNullOrEmpty(ovpn.Cipher)) lines.Add($"cipher {ovpn.Cipher}");
            if (!string.IsNullOrEmpty(ovpn.Auth)) lines.Add($"auth {ovpn.Auth}");

            if (!string.IsNullOrEmpty(ovpn.CaCert))
            {
                lines.Add("<ca>");
                lines.Add(ovpn.CaCert.Trim());
                lines.Add("</ca>");
            }

            if (!string.IsNullOrEmpty(ovpn.ClientCert))
            {
                lines.Add("<cert>");
                lines.Add(ovpn.ClientCert.Trim());
                lines.Add("</cert>");
            }

            if (!string.IsNullOrEmpty(ovpn.ClientKey))
            {
                lines.Add("<key>");
                lines.Add(ovpn.ClientKey.Trim());
                lines.Add("</key>");
            }

            if (!string.IsNullOrEmpty(ovpn.TlsAuthKey))
            {
                if (!string.IsNullOrEmpty(ovpn.KeyDirection)) lines.Add($"key-direction {ovpn.KeyDirection}");
                lines.Add("<tls-auth>");
                lines.Add(ovpn.TlsAuthKey.Trim());
                lines.Add("</tls-auth>");
            }
            else if (!string.IsNullOrEmpty(ovpn.TlsCryptKey))
            {
                lines.Add("<tls-crypt>");
                lines.Add(ovpn.TlsCryptKey.Trim());
                lines.Add("</tls-crypt>");
            }
            else if (!string.IsNullOrEmpty(ovpn.TlsCryptV2Key))
            {
                lines.Add("<tls-crypt-v2>");
                lines.Add(ovpn.TlsCryptV2Key.Trim());
                lines.Add("</tls-crypt-v2>");
            }
        }

        return string.Join("\n", lines) + "\n";
    }

    // ======================================================================
    // WIREGUARD ENGINE EXECUTION (SECONDARY TRANSPORT)
    // ======================================================================

    private (bool Success, string? ErrorCode, string Message) StartWireGuard(TunnelConfiguration config)
    {
        var engineExe = FindEngineExecutable();
        if (string.IsNullOrEmpty(engineExe) || !File.Exists(engineExe))
        {
            _logger.Error("[WireGuard] Could not locate dcss-engine.exe binary.");
            return (false, "EngineNotFound", "Could not locate routing engine binary (dcss-engine.exe).");
        }

        var configJson = GenerateEngineConfig(config);
        var configPath = Path.Combine(_workingDirectory, "engine_config.json");
        File.WriteAllText(configPath, configJson);

        var psi = new ProcessStartInfo
        {
            FileName = engineExe,
            Arguments = $"run -c \"{configPath}\"",
            WorkingDirectory = _workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        _engineProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };

        _engineProcess.OutputDataReceived += (s, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _logger.Debug($"[Engine Stdout] {Sanitizer.Sanitize(e.Data)}");
            }
        };

        _engineProcess.ErrorDataReceived += (s, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _logger.Warning($"[Engine Stderr] {Sanitizer.Sanitize(e.Data)}");
            }
        };

        if (!_engineProcess.Start())
        {
            return (false, "EngineStartFailed", "Failed to start dcss-engine process.");
        }

        _engineProcess.BeginOutputReadLine();
        _engineProcess.BeginErrorReadLine();

        if (_engineProcess.WaitForExit(600))
        {
            var exitCode = _engineProcess.ExitCode;
            return (false, "EngineExitedEarly", $"Routing engine terminated unexpectedly (exit code {exitCode}).");
        }

        // Start WinDivert process isolation pointing target traffic to local proxy transport
        _isolationEngine.StartAsync(new ProcessIsolationOptions
        {
            TargetProcessNames = config.AllowedApps ?? new List<string> { "Discord.exe", "DiscordPTB.exe", "DiscordCanary.exe" },
            TransportType = "WireGuard"
        }).GetAwaiter().GetResult();

        _logger.Info("[WireGuard] WireGuard local proxy transport and WinDivert process isolation active.");
        return (true, null, "WireGuard tunnel active.");
    }

    public void Stop()
    {
        lock (_engineLock)
        {
            try
            {
                _isolationEngine.StopAsync().GetAwaiter().GetResult();
            }
            catch { }

            if (_openVpnProcess != null)
            {
                try
                {
                    if (!_openVpnProcess.HasExited)
                    {
                        _logger.Info($"Stopping OpenVPN (PID: {_openVpnProcess.Id})...");
                        _openVpnProcess.Kill(entireProcessTree: true);
                        _openVpnProcess.WaitForExit(2000);
                    }
                }
                catch { }
                finally
                {
                    _openVpnProcess.Dispose();
                    _openVpnProcess = null;
                }
            }

            if (_engineProcess != null)
            {
                try
                {
                    if (!_engineProcess.HasExited)
                    {
                        _logger.Info($"Stopping dcss-engine (PID: {_engineProcess.Id})...");
                        _engineProcess.Kill(entireProcessTree: true);
                        _engineProcess.WaitForExit(2000);
                    }
                }
                catch { }
                finally
                {
                    _engineProcess.Dispose();
                    _engineProcess = null;
                }
            }

            CleanupTempFiles();
            _logger.Info("Routing engine stopped.");
        }
    }

    private void CleanupTempFiles()
    {
        if (!string.IsNullOrEmpty(_tempCredentialsPath) && File.Exists(_tempCredentialsPath))
        {
            try { File.Delete(_tempCredentialsPath); } catch { }
            _tempCredentialsPath = null;
        }

        if (!string.IsNullOrEmpty(_tempOvpnConfigPath) && File.Exists(_tempOvpnConfigPath))
        {
            try { File.Delete(_tempOvpnConfigPath); } catch { }
            _tempOvpnConfigPath = null;
        }
    }

    public ProcessIsolationStats GetIsolationStats()
    {
        return _isolationEngine.GetStats();
    }

    public (bool IsValid, string? Error) ValidateRuntimeConfiguration(TunnelConfiguration config)
    {
        if (string.Equals(config.Protocol, VpnProtocol.OpenVpn, StringComparison.OrdinalIgnoreCase))
        {
            return (true, null);
        }

        try
        {
            var engineExe = FindEngineExecutable();
            if (string.IsNullOrEmpty(engineExe) || !File.Exists(engineExe))
            {
                return (true, null);
            }

            var configJson = GenerateEngineConfig(config);
            var tempConfigPath = Path.Combine(_workingDirectory, $"validate_{Guid.NewGuid():N}.json");
            File.WriteAllText(tempConfigPath, configJson);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = engineExe,
                    Arguments = $"check -c \"{tempConfigPath}\"",
                    WorkingDirectory = _workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) return (true, null);

                var err = proc.StandardError.ReadToEnd();
                var stdout = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(3000);

                if (proc.ExitCode == 0) return (true, null);
                var combined = string.IsNullOrWhiteSpace(err) ? stdout : err;
                return (false, Sanitizer.Sanitize(combined.Trim()));
            }
            finally
            {
                try { File.Delete(tempConfigPath); } catch { }
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private string FindOpenVpnExecutable()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "openvpn", "openvpn.exe"),
            Path.Combine(baseDir, "openvpn.exe"),
            Path.Combine(Directory.GetCurrentDirectory(), "runtimes", "win-x64", "openvpn", "openvpn.exe"),
            Path.Combine(Directory.GetCurrentDirectory(), "openvpn", "openvpn.exe"),
            @"C:\Program Files\DC-ScreenSharing\openvpn\openvpn.exe",
            @"D:\DC-ScreenSharing\runtimes\win-x64\openvpn\openvpn.exe",
            @"C:\Program Files\OpenVPN\bin\openvpn.exe"
        };

        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }

    private string FindEngineExecutable()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "native", "dcss-engine.exe"),
            Path.Combine(baseDir, "dcss-engine.exe"),
            Path.Combine(Directory.GetCurrentDirectory(), "runtimes", "win-x64", "native", "dcss-engine.exe"),
            Path.Combine(Directory.GetCurrentDirectory(), "native", "dcss-engine.exe"),
            @"C:\Program Files\DC-ScreenSharing\native\dcss-engine.exe",
            @"D:\DC-ScreenSharing\runtimes\win-x64\native\dcss-engine.exe"
        };

        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }

    private string? EnsureWintunDll(string engineDir)
    {
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var wintunSources = new[]
            {
                Path.Combine(baseDir, "native", "wintun.dll"),
                Path.Combine(baseDir, "wintun.dll"),
                Path.Combine(engineDir, "wintun.dll"),
                Path.Combine(Directory.GetCurrentDirectory(), "runtimes", "win-x64", "native", "wintun.dll"),
                @"C:\Program Files\DC-ScreenSharing\native\wintun.dll",
                @"D:\DC-ScreenSharing\runtimes\win-x64\native\wintun.dll"
            };

            var source = wintunSources.FirstOrDefault(File.Exists);
            if (source != null)
            {
                var destInWorking = Path.Combine(_workingDirectory, "wintun.dll");
                if (!File.Exists(destInWorking))
                {
                    File.Copy(source, destInWorking, overwrite: true);
                }

                var destInEngineDir = Path.Combine(engineDir, "wintun.dll");
                if (!File.Exists(destInEngineDir))
                {
                    try { File.Copy(source, destInEngineDir, overwrite: true); } catch { }
                }

                return source;
            }
        }
        catch { }

        return null;
    }

    public string GenerateEngineConfig(TunnelConfiguration config, string? openVpnAdapterName = null)
    {
        var processList = new List<string>(config.AllowedApps ?? new List<string>());
        var standardDiscordExes = new[] { "Discord.exe", "DiscordPTB.exe", "DiscordCanary.exe", "DiscordDevelopment.exe" };
        foreach (var name in standardDiscordExes)
        {
            if (!processList.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                processList.Add(name);
            }
        }

        var isOvpn = string.Equals(config.Protocol, VpnProtocol.OpenVpn, StringComparison.OrdinalIgnoreCase);
        string targetOutboundTag = isOvpn ? "ovpn-out" : "wg-out";

        var addresses = new List<string>();
        if (config.Addresses != null && config.Addresses.Count > 0)
        {
            addresses.AddRange(config.Addresses.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()));
        }
        else if (!string.IsNullOrWhiteSpace(config.Address))
        {
            addresses.AddRange(config.Address.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(a => a.Trim()));
        }
        if (addresses.Count == 0) addresses.Add("10.8.0.2/32");

        var allowedIpsList = new List<string>();
        if (config.AllowedIpsList != null && config.AllowedIpsList.Count > 0)
        {
            allowedIpsList.AddRange(config.AllowedIpsList.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()));
        }
        else if (!string.IsNullOrWhiteSpace(config.AllowedIps))
        {
            allowedIpsList.AddRange(config.AllowedIps.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(a => a.Trim()));
        }
        if (allowedIpsList.Count == 0) allowedIpsList.AddRange(new[] { "0.0.0.0/0", "::/0" });

        var keepaliveInterval = config.PersistentKeepalive > 0 ? config.PersistentKeepalive : 25;

        // Loopback Mixed (SOCKS5/HTTP) Inbound — Zero TUN, Zero Host Route modifications
        var inbounds = new object[]
        {
            new
            {
                type = "mixed",
                tag = "proxy-in",
                listen = "127.0.0.1",
                listen_port = 15888
            }
        };

        var outbounds = new List<object>
        {
            new { type = "direct", tag = "direct" }
        };

        object[]? endpoints = null;

        if (isOvpn)
        {
            outbounds.Add(new
            {
                type = "direct",
                tag = "ovpn-out",
                bind_interface = openVpnAdapterName ?? "OpenVPN"
            });
        }
        else
        {
            endpoints = new object[]
            {
                new
                {
                    type = "wireguard",
                    tag = "wg-out",
                    address = addresses.ToArray(),
                    private_key = config.PrivateKey ?? string.Empty,
                    peers = new object[]
                    {
                        new
                        {
                            address = config.Endpoint,
                            port = config.Port,
                            public_key = config.PeerPublicKey ?? string.Empty,
                            allowed_ips = allowedIpsList.ToArray(),
                            persistent_keepalive_interval = keepaliveInterval
                        }
                    },
                    mtu = config.Mtu > 0 ? config.Mtu : 1420
                }
            };
        }

        var configObj = new
        {
            log = new { level = "warn", timestamp = true },
            inbounds = inbounds,
            endpoints = endpoints,
            outbounds = outbounds.ToArray(),
            route = new
            {
                rules = new object[]
                {
                    new { inbound = new[] { "proxy-in" }, outbound = targetOutboundTag }
                },
                final = "direct"
            }
        };

        return JsonSerializer.Serialize(configObj, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        await _isolationEngine.DisposeAsync();
    }
}
