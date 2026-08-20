namespace DCScreenSharing.Shared;

public static class Constants
{
    public const string AppName = "DC-ScreenSharing";
    public const string ServiceName = "DCSS.NetworkService";
    public const string ServiceDisplayName = "DC-ScreenSharing Network Service";
    public const string ServiceDescription = "Provides privileged application-specific network routing for DC-ScreenSharing.";

    public const string GitHubOwner = "FaelSemW";
    public const string GitHubRepository = "DC-ScreenSharing";
    public const string CurrentVersion = "1.0.8";
    public const string GitHubReleasesApiUrl = "https://api.github.com/repos/FaelSemW/DC-ScreenSharing/releases/latest";

    public const string PipeName = "DCSS_NetworkService_Pipe";
    public const string FullPipePath = @"\\.\pipe\" + PipeName;

    public const string AppMutexName = "Global\\DC_ScreenSharing_App_SingleInstance_Mutex";
    public const string ServiceMutexName = "Global\\DC_ScreenSharing_Service_SingleInstance_Mutex";

    public const string DefaultInterfaceName = "dcss-wintun";
    public const int DefaultInterfaceMtu = 1420;

    public const int SchemaVersion = 1;
    public const int ComponentVersion = 1;

    public const string DefaultProfileServiceUrl = "https://zaprecovery.online";
    public const string DefaultCatalogUrl = DefaultProfileServiceUrl + "/api/v1/catalog";

    public const int ProfileRenewalThresholdHours = 24;
    public const int DefaultProfileLifetimeDays = 7;
}
