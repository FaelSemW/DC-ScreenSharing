namespace DCScreenSharing.Shared.Logging;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3
}

public interface IAppLogger
{
    void Debug(string message);
    void Info(string message);
    void Warning(string message, Exception? ex = null);
    void Error(string message, Exception? ex = null);
    IReadOnlyList<string> GetRecentLogs(int count = 50);
}

public class FileLogger : IAppLogger
{
    private readonly string _logFilePath;
    private readonly object _lock = new();
    private readonly Queue<string> _recentMemoryLogs = new(100);
    private readonly LogLevel _minLevel;

    public FileLogger(string logDirectory, string logFileName = "app.log", LogLevel minLevel = LogLevel.Info)
    {
        _minLevel = minLevel;
        try
        {
            Directory.CreateDirectory(logDirectory);
            _logFilePath = Path.Combine(logDirectory, logFileName);
            RotateIfNeeded();
        }
        catch
        {
            _logFilePath = Path.Combine(Path.GetTempPath(), logFileName);
        }
    }

    public void Debug(string message) => Log(LogLevel.Debug, message);
    public void Info(string message) => Log(LogLevel.Info, message);
    public void Warning(string message, Exception? ex = null) => Log(LogLevel.Warning, message, ex);
    public void Error(string message, Exception? ex = null) => Log(LogLevel.Error, message, ex);

    public IReadOnlyList<string> GetRecentLogs(int count = 50)
    {
        lock (_lock)
        {
            return _recentMemoryLogs.TakeLast(count).ToList();
        }
    }

    private void Log(LogLevel level, string message, Exception? ex = null)
    {
        if (level < _minLevel)
            return;

        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var sanitizedMsg = Sanitizer.Sanitize(message);
        var exString = ex != null ? $" | Exception: {Sanitizer.Sanitize(ex.Message)}" : string.Empty;
        var formatted = $"[{timestamp}] [{level.ToString().ToUpperInvariant()}] {sanitizedMsg}{exString}";

        lock (_lock)
        {
            try
            {
                if (_recentMemoryLogs.Count >= 100)
                    _recentMemoryLogs.Dequeue();
                _recentMemoryLogs.Enqueue(formatted);

                File.AppendAllText(_logFilePath, formatted + Environment.NewLine);
            }
            catch
            {
                // Fallback to stderr or ignore if logging system temporarily fails
            }
        }
    }

    private void RotateIfNeeded()
    {
        try
        {
            if (File.Exists(_logFilePath))
            {
                var fileInfo = new FileInfo(_logFilePath);
                // Rotate if greater than 5 MB
                if (fileInfo.Length > 5 * 1024 * 1024)
                {
                    var backupPath = _logFilePath + ".old";
                    if (File.Exists(backupPath))
                        File.Delete(backupPath);
                    File.Move(_logFilePath, backupPath);
                }
            }
        }
        catch
        {
            // Ignore rotation errors
        }
    }
}
