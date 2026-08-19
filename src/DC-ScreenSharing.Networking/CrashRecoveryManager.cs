using System.Text.Json;
using DCScreenSharing.Shared;
using DCScreenSharing.Shared.Contracts;
using DCScreenSharing.Shared.Logging;

namespace DCScreenSharing.Networking;

public class NetworkStateRecord
{
    public bool TunnelActive { get; set; }
    public string? ActiveServerId { get; set; }
    public string? InterfaceName { get; set; }
    public int? EnginePid { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}

public class CrashRecoveryManager
{
    private readonly string _stateFilePath;
    private readonly IAppLogger _logger;
    private readonly object _lock = new();

    public CrashRecoveryManager(IAppLogger logger, string? stateDirectory = null)
    {
        _logger = logger;
        var dir = stateDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DC-ScreenSharing");
        _stateFilePath = Path.Combine(dir, "net_state.json");
        try { Directory.CreateDirectory(dir); } catch { }
    }

    public void RecordState(bool active, string? serverId, string? interfaceName, int? pid)
    {
        lock (_lock)
        {
            try
            {
                var record = new NetworkStateRecord
                {
                    TunnelActive = active,
                    ActiveServerId = serverId,
                    InterfaceName = interfaceName ?? Constants.DefaultInterfaceName,
                    EnginePid = pid,
                    TimestampUtc = DateTime.UtcNow
                };

                var json = JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_stateFilePath, json);
            }
            catch (Exception ex)
            {
                _logger.Warning("Failed to persist network state record.", ex);
            }
        }
    }

    public void ClearState()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_stateFilePath))
                {
                    File.Delete(_stateFilePath);
                }
            }
            catch { }
        }
    }

    public void PerformStartupRecovery(ProcessRoutingEngine engine)
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_stateFilePath))
                    return;

                var json = File.ReadAllText(_stateFilePath);
                var record = JsonSerializer.Deserialize<NetworkStateRecord>(json);

                if (record != null && record.TunnelActive)
                {
                    _logger.Warning($"Detected incomplete previous session from {record.TimestampUtc:u} (Server: {record.ActiveServerId}). Performing safe cleanup of DC-ScreenSharing resources.");

                    // Check if orphaned engine PID exists and kill it
                    if (record.EnginePid.HasValue)
                    {
                        try
                        {
                            var proc = System.Diagnostics.Process.GetProcessById(record.EnginePid.Value);
                            if (proc.ProcessName.Contains("dcss-engine", StringComparison.OrdinalIgnoreCase) ||
                                proc.ProcessName.Contains("sing-box", StringComparison.OrdinalIgnoreCase))
                            {
                                _logger.Info($"Terminating orphaned routing engine PID {record.EnginePid.Value}...");
                                proc.Kill(entireProcessTree: true);
                            }
                        }
                        catch { }
                    }

                    engine.Stop();
                    ClearState();
                    _logger.Info("Cleanup of previous session completed successfully.");
                }
            }
            catch (Exception ex)
            {
                _logger.Warning("Error during startup crash recovery check.", ex);
            }
        }
    }
}
