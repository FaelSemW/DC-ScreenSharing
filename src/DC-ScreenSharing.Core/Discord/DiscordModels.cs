namespace DCScreenSharing.Core.Discord;

public enum DiscordFlavor
{
    Automatic = 0,
    Stable = 1,
    PTB = 2,
    Canary = 3,
    Development = 4
}

public class DiscordInstallation
{
    public DiscordFlavor Flavor { get; set; }
    public string InstallationDirectory { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string? UpdateExePath { get; set; }
    public Version Version { get; set; } = new(0, 0, 0);
    public string VersionString => Version.ToString();
    public bool IsRunning { get; set; }
    public List<int> RunningProcessIds { get; set; } = new();

    public override string ToString() =>
        $"{Flavor} (v{VersionString}) - {ExecutablePath} [Running: {IsRunning}]";
}
