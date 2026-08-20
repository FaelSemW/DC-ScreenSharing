using System.Collections.Concurrent;
using System.Text.Json;

namespace DCSS.ProfileService.Services;

public class AuditEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string Action { get; set; } = string.Empty;
    public string Actor { get; set; } = "Admin";
    public string ClientIp { get; set; } = string.Empty;
    public string? TargetId { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public class AuditLogService
{
    private readonly string _storagePath;
    private readonly ConcurrentBag<AuditEvent> _events = new();
    private readonly object _lock = new();

    public AuditLogService(IConfiguration config)
    {
        var basePath = config["ProfileService:StoragePath"] ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "storage");
        Directory.CreateDirectory(basePath);
        _storagePath = Path.Combine(basePath, "audit_log.json");

        LoadState();
    }

    private void LoadState()
    {
        lock (_lock)
        {
            if (File.Exists(_storagePath))
            {
                try
                {
                    var json = File.ReadAllText(_storagePath);
                    var list = JsonSerializer.Deserialize<List<AuditEvent>>(json);
                    if (list != null)
                    {
                        foreach (var evt in list)
                        {
                            _events.Add(evt);
                        }
                    }
                }
                catch { }
            }
        }
    }

    private void SaveState()
    {
        lock (_lock)
        {
            try
            {
                var list = _events.OrderByDescending(e => e.TimestampUtc).Take(500).ToList();
                var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_storagePath, json);
            }
            catch { }
        }
    }

    public void Record(string action, string actor, string clientIp, string? targetId = null, Dictionary<string, string>? metadata = null)
    {
        var evt = new AuditEvent
        {
            Action = action,
            Actor = actor,
            ClientIp = clientIp,
            TargetId = targetId,
            Metadata = metadata ?? new()
        };

        _events.Add(evt);
        SaveState();
    }

    public IReadOnlyList<AuditEvent> GetEvents(int limit = 100)
    {
        return _events.OrderByDescending(e => e.TimestampUtc).Take(limit).ToList();
    }
}
