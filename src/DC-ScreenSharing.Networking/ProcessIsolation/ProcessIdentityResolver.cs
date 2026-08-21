using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace DC_ScreenSharing.Networking.ProcessIsolation;

public class TargetProcessInfo
{
    public int ProcessId { get; set; }
    public string ExecutablePath { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public DateTime StartTimeUtc { get; set; }
}

public class ProcessIdentityResolver
{
    private readonly ConcurrentDictionary<int, TargetProcessInfo> _knownTargetPids = new();
    private readonly ConcurrentDictionary<string, string> _discoveredTargetPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _targetNames = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastDiscoveryTime = DateTime.MinValue;
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(5);

    public ProcessIdentityResolver(IEnumerable<string>? targetNames = null)
    {
        if (targetNames != null)
        {
            foreach (var name in targetNames)
            {
                _targetNames.Add(name);
            }
        }
        else
        {
            _targetNames.Add("Discord.exe");
            _targetNames.Add("DiscordPTB.exe");
            _targetNames.Add("DiscordCanary.exe");
        }

        RefreshKnownProcessPaths();
    }

    public void UpdateTargetNames(IEnumerable<string> targetNames)
    {
        lock (_targetNames)
        {
            _targetNames.Clear();
            foreach (var name in targetNames)
            {
                _targetNames.Add(name);
            }
        }
        RefreshKnownProcessPaths();
    }

    public void RefreshKnownProcessPaths()
    {
        try
        {
            // Discover standard Discord install paths
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
            {
                var discordDirs = new[]
                {
                    Path.Combine(localAppData, "Discord"),
                    Path.Combine(localAppData, "DiscordPTB"),
                    Path.Combine(localAppData, "DiscordCanary")
                };

                foreach (var dir in discordDirs)
                {
                    if (Directory.Exists(dir))
                    {
                        var appDirs = Directory.GetDirectories(dir, "app-*");
                        foreach (var appDir in appDirs)
                        {
                            var exeFiles = Directory.GetFiles(appDir, "Discord*.exe");
                            foreach (var exe in exeFiles)
                            {
                                var fileName = Path.GetFileName(exe);
                                _discoveredTargetPaths[fileName] = exe;
                            }
                        }
                    }
                }
            }

            // Scan running processes matching target names
            lock (_targetNames)
            {
                foreach (var targetName in _targetNames)
                {
                    var nameWithoutExt = Path.GetFileNameWithoutExtension(targetName);
                    var processes = Process.GetProcessesByName(nameWithoutExt);
                    foreach (var proc in processes)
                    {
                        try
                        {
                            var mainModulePath = proc.MainModule?.FileName;
                            if (!string.IsNullOrEmpty(mainModulePath))
                            {
                                var fileName = Path.GetFileName(mainModulePath);
                                _discoveredTargetPaths[fileName] = mainModulePath;
                                RegisterTargetPid(proc.Id, mainModulePath, proc.StartTime.ToUniversalTime());
                            }
                        }
                        catch { }
                        finally
                        {
                            proc.Dispose();
                        }
                    }
                }
            }

            _lastDiscoveryTime = DateTime.UtcNow;
        }
        catch { }
    }

    public bool IsTargetProcess(int pid)
    {
        if (pid <= 4) return false;

        if (DateTime.UtcNow - _lastDiscoveryTime > DiscoveryInterval)
        {
            RefreshKnownProcessPaths();
        }

        if (_knownTargetPids.TryGetValue(pid, out var targetInfo))
        {
            // Verify PID has not been reused by validating start time / process existence
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (proc.HasExited)
                {
                    _knownTargetPids.TryRemove(pid, out _);
                    return false;
                }

                // If process start time differs from recorded start time, PID was reused
                if (Math.Abs((proc.StartTime.ToUniversalTime() - targetInfo.StartTimeUtc).TotalSeconds) > 2)
                {
                    _knownTargetPids.TryRemove(pid, out _);
                    return false;
                }

                return true;
            }
            catch (ArgumentException)
            {
                if ((DateTime.UtcNow - targetInfo.StartTimeUtc).TotalSeconds < 10)
                {
                    return true;
                }
                _knownTargetPids.TryRemove(pid, out _);
                return false;
            }
            catch
            {
                _knownTargetPids.TryRemove(pid, out _);
                return false;
            }
        }

        // Check if process is a newly spawned target instance
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited) return false;

            var processName = proc.ProcessName;
            var fileNameWithExt = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName
                : processName + ".exe";

            bool isMatch;
            lock (_targetNames)
            {
                isMatch = _targetNames.Contains(fileNameWithExt);
            }

            if (isMatch)
            {
                string exePath = string.Empty;
                try { exePath = proc.MainModule?.FileName ?? string.Empty; } catch { }
                RegisterTargetPid(pid, exePath, proc.StartTime.ToUniversalTime());
                return true;
            }
        }
        catch { }

        return false;
    }

    public void RegisterTargetPid(int pid, string exePath, DateTime startTimeUtc)
    {
        var procName = Path.GetFileName(exePath);
        _knownTargetPids[pid] = new TargetProcessInfo
        {
            ProcessId = pid,
            ExecutablePath = exePath,
            ProcessName = procName,
            StartTimeUtc = startTimeUtc
        };
    }

    public void RegisterPid(int pid, string procName = "", string exePath = "")
    {
        _knownTargetPids[pid] = new TargetProcessInfo
        {
            ProcessId = pid,
            ExecutablePath = exePath,
            ProcessName = string.IsNullOrEmpty(procName) ? $"PID_{pid}" : procName,
            StartTimeUtc = DateTime.UtcNow
        };
    }

    public void UnregisterPid(int pid)
    {
        _knownTargetPids.TryRemove(pid, out _);
    }

    public int GetTrackedPidCount() => _knownTargetPids.Count;

    // ======================================================================
    // EXTENDED TCP / UDP TABLE LOOKUPS
    // ======================================================================

    public int? FindPidForTcpSocket(IPAddress localIp, ushort localPort, IPAddress remoteIp, ushort remotePort)
    {
        try
        {
            var table = GetExtendedTcpTable();
            foreach (var row in table)
            {
                if (row.LocalPort == localPort && (row.RemotePort == remotePort || row.RemotePort == 0))
                {
                    if (row.LocalAddress.Equals(localIp) || localIp.Equals(IPAddress.Any) || row.LocalAddress.Equals(IPAddress.Any))
                    {
                        return (int)row.OwningPid;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    public int? FindPidForUdpSocket(IPAddress localIp, ushort localPort)
    {
        try
        {
            var table = GetExtendedUdpTable();
            foreach (var row in table)
            {
                if (row.LocalPort == localPort)
                {
                    if (row.LocalAddress.Equals(localIp) || localIp.Equals(IPAddress.Any) || row.LocalAddress.Equals(IPAddress.Any))
                    {
                        return (int)row.OwningPid;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    public struct MIB_TCPROW_OWNER_PID
    {
        public uint State;
        public uint LocalAddr;
        public byte LocalPort1;
        public byte LocalPort2;
        public byte LocalPort3;
        public byte LocalPort4;
        public uint RemoteAddr;
        public byte RemotePort1;
        public byte RemotePort2;
        public byte RemotePort3;
        public byte RemotePort4;
        public uint OwningPid;

        public ushort LocalPort => (ushort)((LocalPort1 << 8) | LocalPort2);
        public ushort RemotePort => (ushort)((RemotePort1 << 8) | RemotePort2);
        public IPAddress LocalAddress => new IPAddress(LocalAddr);
        public IPAddress RemoteAddress => new IPAddress(RemoteAddr);
    }

    public struct MIB_UDPROW_OWNER_PID
    {
        public uint LocalAddr;
        public byte LocalPort1;
        public byte LocalPort2;
        public byte LocalPort3;
        public byte LocalPort4;
        public uint OwningPid;

        public ushort LocalPort => (ushort)((LocalPort1 << 8) | LocalPort2);
        public IPAddress LocalAddress => new IPAddress(LocalAddr);
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        bool bOrder,
        uint ulAf,
        TCP_TABLE_CLASS TableClass,
        uint Reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable,
        ref int pdwSize,
        bool bOrder,
        uint ulAf,
        UDP_TABLE_CLASS TableClass,
        uint Reserved);

    private enum TCP_TABLE_CLASS
    {
        TCP_TABLE_BASIC_LISTENER,
        TCP_TABLE_BASIC_CONNECTIONS,
        TCP_TABLE_BASIC_ALL,
        TCP_TABLE_OWNER_PID_LISTENER,
        TCP_TABLE_OWNER_PID_CONNECTIONS,
        TCP_TABLE_OWNER_PID_ALL,
        TCP_TABLE_OWNER_MODULE_LISTENER,
        TCP_TABLE_OWNER_MODULE_CONNECTIONS,
        TCP_TABLE_OWNER_MODULE_ALL
    }

    private enum UDP_TABLE_CLASS
    {
        UDP_TABLE_BASIC,
        UDP_TABLE_OWNER_PID,
        UDP_TABLE_OWNER_MODULE
    }

    private const uint AF_INET = 2;

    public static List<MIB_TCPROW_OWNER_PID> GetExtendedTcpTable()
    {
        var list = new List<MIB_TCPROW_OWNER_PID>();
        int size = 0;
        uint res = GetExtendedTcpTable(IntPtr.Zero, ref size, true, AF_INET, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0);

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            res = GetExtendedTcpTable(buffer, ref size, true, AF_INET, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0);
            if (res == 0)
            {
                int numEntries = Marshal.ReadInt32(buffer);
                IntPtr rowPtr = IntPtr.Add(buffer, 4);
                int structSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();

                for (int i = 0; i < numEntries; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                    list.Add(row);
                    rowPtr = IntPtr.Add(rowPtr, structSize);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return list;
    }

    public static List<MIB_UDPROW_OWNER_PID> GetExtendedUdpTable()
    {
        var list = new List<MIB_UDPROW_OWNER_PID>();
        int size = 0;
        uint res = GetExtendedUdpTable(IntPtr.Zero, ref size, true, AF_INET, UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID, 0);

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            res = GetExtendedUdpTable(buffer, ref size, true, AF_INET, UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID, 0);
            if (res == 0)
            {
                int numEntries = Marshal.ReadInt32(buffer);
                IntPtr rowPtr = IntPtr.Add(buffer, 4);
                int structSize = Marshal.SizeOf<MIB_UDPROW_OWNER_PID>();

                for (int i = 0; i < numEntries; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(rowPtr);
                    list.Add(row);
                    rowPtr = IntPtr.Add(rowPtr, structSize);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return list;
    }
}
