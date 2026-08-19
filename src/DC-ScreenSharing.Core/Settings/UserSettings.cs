using System.Text.Json;
using DCScreenSharing.Core.Discord;
using DCScreenSharing.Shared.Logging;

namespace DCScreenSharing.Core.Settings;

public class UserSettings
{
    public DiscordFlavor PreferredFlavor { get; set; } = DiscordFlavor.Automatic;
    public bool AutoLaunchDiscord { get; set; } = true;
    public bool StartMinimized { get; set; } = false;
    public bool StartWithWindows { get; set; } = false;
    public string? LastSelectedServerId { get; set; }
}

public class SettingsManager
{
    private readonly string _settingsFilePath;
    private readonly IAppLogger _logger;
    private readonly object _lock = new();

    public SettingsManager(string? baseDir = null, IAppLogger? logger = null)
    {
        _logger = logger ?? new FileLogger(Path.GetTempPath());
        var root = baseDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DC-ScreenSharing");
        _settingsFilePath = Path.Combine(root, "settings.json");

        try
        {
            Directory.CreateDirectory(root);
        }
        catch { }
    }

    public UserSettings Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    var json = File.ReadAllText(_settingsFilePath);
                    return JsonSerializer.Deserialize<UserSettings>(json) ?? new UserSettings();
                }
            }
            catch (Exception ex)
            {
                _logger.Warning("Could not load user settings file. Using defaults.", ex);
            }

            return new UserSettings();
        }
    }

    public void Save(UserSettings settings)
    {
        lock (_lock)
        {
            try
            {
                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsFilePath, json);
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to save user settings.", ex);
            }
        }
    }
}
