using System.Diagnostics;
using System.IO;
using System.Text.Json;
using DCScreenSharing.Shared;
using DCScreenSharing.Shared.Contracts;
using DCScreenSharing.Shared.Logging;

namespace DCScreenSharing.Networking;

public class ProcessRoutingEngine
{
    private readonly IAppLogger _logger;
    private readonly string _workingDirectory;
    private Process? _engineProcess;
    private readonly object _engineLock = new();

    public bool IsRunning
    {
        get
        {
            lock (_engineLock)
            {
                return _engineProcess != null && !_engineProcess.HasExited;
            }
        }
    }

    public ProcessRoutingEngine(IAppLogger logger, string? workingDirectory = null)
    {
        _logger = logger;
        _workingDirectory = workingDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DC-ScreenSharing");
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
                // 1. Stage: engine path resolved
                var engineExe = FindEngineExecutable();
                if (string.IsNullOrEmpty(engineExe) || !File.Exists(engineExe))
                {
                    _logger.Error($"[Stage: EngineResolution] Could not locate dcss-engine.exe binary. Searched base: {AppDomain.CurrentDomain.BaseDirectory}");
                    return (false, "EngineNotFound", "Could not locate routing engine binary (dcss-engine.exe).");
                }
                _logger.Info($"[Stage: EngineResolution] Resolved engine path: {engineExe}");

                // 2. Stage: Wintun path resolved
                var wintunDll = EnsureWintunDll(Path.GetDirectoryName(engineExe)!);
                _logger.Info($"[Stage: WintunResolution] Resolved Wintun driver path: {wintunDll ?? "NotFound"}");

                // 3. Stage: runtime config generated
                var configJson = GenerateEngineConfig(config);
                var configPath = Path.Combine(_workingDirectory, "engine_config.json");
                File.WriteAllText(configPath, configJson);
                _logger.Info($"[Stage: ConfigGenerated] Generated engine configuration at: {configPath}");

                // 4. Stage: validate config with engine check
                var validationResult = ValidateConfig(engineExe, configPath);
                if (!validationResult.IsValid)
                {
                    _logger.Error($"[Stage: ConfigValidation] Configuration validation failed: {validationResult.Error}");
                    return (false, "InvalidRuntimeConfig", $"Routing engine rejected configuration: {validationResult.Error}");
                }
                _logger.Info("[Stage: ConfigValidation] Configuration passed sing-box validation.");

                // 5. Stage: engine process starting
                _logger.Info($"[Stage: ProcessStart] Launching dcss-engine for server '{config.ServerId}' (Endpoint: {config.Endpoint}:{config.Port})...");

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

                var started = _engineProcess.Start();
                if (!started)
                {
                    _logger.Error("[Stage: ProcessStart] Failed to start dcss-engine process.");
                    return (false, "EngineStartFailed", "Failed to start dcss-engine process.");
                }

                _engineProcess.BeginOutputReadLine();
                _engineProcess.BeginErrorReadLine();

                _logger.Info($"[Stage: ProcessStarted] dcss-engine started (PID: {_engineProcess.Id}).");

                // 6. Stage: engine readiness check
                _logger.Info("[Stage: ReadinessCheck] Performing engine readiness check...");
                if (_engineProcess.WaitForExit(600))
                {
                    var exitCode = _engineProcess.ExitCode;
                    _logger.Error($"[Stage: ReadinessCheck] dcss-engine exited immediately with code {exitCode}.");
                    return (false, "EngineExitedEarly", $"Routing engine terminated unexpectedly (exit code {exitCode}).");
                }

                _logger.Info("[Stage: ReadinessCheck] dcss-engine is active and running.");
                _logger.Info("[Stage: RouteRulesActive] Split-tunneling rules are active.");

                return (true, null, "Tunnel active.");
            }
            catch (Exception ex)
            {
                _logger.Error("[Stage: Error] Exception while starting routing engine", ex);
                return (false, "InternalEngineError", $"Engine error: {ex.Message}");
            }
        }
    }

    public void Stop()
    {
        lock (_engineLock)
        {
            if (_engineProcess != null)
            {
                try
                {
                    if (!_engineProcess.HasExited)
                    {
                        _logger.Info($"Stopping dcss-engine (PID: {_engineProcess.Id})...");
                        _engineProcess.Kill(entireProcessTree: true);
                        _engineProcess.WaitForExit(3000);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning("Error stopping dcss-engine process", ex);
                }
                finally
                {
                    _engineProcess.Dispose();
                    _engineProcess = null;
                }
            }

            _logger.Info("Routing engine stopped.");
        }
    }

    private (bool IsValid, string? Error) ValidateConfig(string engineExe, string configPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = engineExe,
                Arguments = $"check -c \"{configPath}\"",
                WorkingDirectory = _workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return (true, null);

            var err = proc.StandardError.ReadToEnd();
            proc.WaitForExit(3000);

            if (proc.ExitCode == 0)
            {
                return (true, null);
            }

            return (false, Sanitizer.Sanitize(err.Trim()));
        }
        catch
        {
            return (true, null); // If check command not supported, proceed to run
        }
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

    public string GenerateEngineConfig(TunnelConfiguration config)
    {
        var processList = new List<string>(config.AllowedApps);
        if (!string.IsNullOrEmpty(config.DiscordExecutablePath))
        {
            var exeName = Path.GetFileName(config.DiscordExecutablePath);
            if (!processList.Contains(exeName, StringComparer.OrdinalIgnoreCase))
            {
                processList.Add(exeName);
            }
        }

        // Include all standard Discord flavor process names
        var standardDiscordExes = new[] { "Discord.exe", "DiscordPTB.exe", "DiscordCanary.exe", "DiscordDevelopment.exe" };
        foreach (var name in standardDiscordExes)
        {
            if (!processList.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                processList.Add(name);
            }
        }

        var allowedIpsList = (config.AllowedIps ?? "0.0.0.0/0, ::/0")
            .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);

        var configObj = new
        {
            log = new
            {
                level = "warn",
                timestamp = true
            },
            dns = new
            {
                servers = new object[]
                {
                    new { tag = "dns-remote", type = "udp", server = "1.1.1.1", server_port = 53, detour = "wg-out" },
                    new { tag = "dns-direct", type = "local", detour = "direct" }
                },
                rules = new object[]
                {
                    new { process_name = processList, server = "dns-remote" }
                },
                final = "dns-direct",
                strategy = "prefer_ipv4"
            },
            inbounds = new object[]
            {
                new
                {
                    type = "tun",
                    tag = "tun-in",
                    interface_name = Constants.DefaultInterfaceName,
                    address = new[] { "172.19.0.1/30", "fdfe:dc::1/126" },
                    auto_route = true,
                    strict_route = false,
                    stack = "system"
                }
            },
            endpoints = new object[]
            {
                new
                {
                    type = "wireguard",
                    tag = "wg-out",
                    address = new[] { config.Address },
                    private_key = config.PrivateKey,
                    peers = new object[]
                    {
                        new
                        {
                            address = config.Endpoint,
                            port = config.Port,
                            public_key = config.PeerPublicKey,
                            allowed_ips = allowedIpsList,
                            persistent_keepalive_interval = 25
                        }
                    },
                    mtu = config.Mtu
                }
            },
            outbounds = new object[]
            {
                new
                {
                    type = "direct",
                    tag = "direct"
                },
                new
                {
                    type = "dns",
                    tag = "dns-out"
                }
            },
            route = new
            {
                auto_detect_interface = true,
                default_domain_resolver = "dns-direct",
                rules = new object[]
                {
                    new
                    {
                        protocol = "dns",
                        outbound = "dns-out"
                    },
                    new
                    {
                        domain_suffix = new[] { "zaprecovery.online", "github.com", "githubusercontent.com" },
                        outbound = "direct"
                    },
                    new
                    {
                        process_name = processList,
                        outbound = "wg-out"
                    },
                    new
                    {
                        outbound = "direct"
                    }
                },
                final = "direct"
            }
        };

        return JsonSerializer.Serialize(configObj, new JsonSerializerOptions { WriteIndented = true });
    }
}
