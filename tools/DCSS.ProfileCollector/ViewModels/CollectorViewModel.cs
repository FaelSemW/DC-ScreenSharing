using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using DCSS.ProfileCollector.Models;
using DCSS.ProfileCollector.Services;

namespace DCSS.ProfileCollector.ViewModels;

public class CollectorViewModel : INotifyPropertyChanged
{
    private readonly VpnBookAutomationService _automationService;
    private readonly ProfileStorageService _storageService;
    private CancellationTokenSource? _cts;

    private VpnBookRegion? _selectedRegion;
    private string _selectedServerMode = "Automatic";
    private PortOption? _selectedPort;
    private int _quantity = 10;
    private string _outputFolder = string.Empty;
    private bool _acceptConditions = false;
    private bool _isGenerating = false;
    private bool _isPaused = false;
    private string _statusMessage = "Ready";
    private string _progressText = "0 / 0 generated";
    private int _progressValue = 0;
    private int _progressMax = 10;
    private bool _showSummary = false;
    private int _summaryRequested;
    private int _summaryGenerated;
    private int _summaryValid;
    private int _summaryDuplicates;
    private int _summaryFailed;
    private string _summaryOutputPath = string.Empty;
    private string _resumeRecommendationText = string.Empty;
    private bool _canResume = false;

    public ObservableCollection<VpnBookRegion> AvailableRegions { get; } = new();
    public ObservableCollection<string> ServerModes { get; } = new() { "Automatic", "Server 1", "Server 2" };
    public ObservableCollection<PortOption> AvailablePorts { get; } = new();
    public ObservableCollection<ProfileResultItem> Results { get; } = new();
    public ObservableCollection<MultiRegionPlanItem> MultiRegionPlan { get; } = new();

    public VpnBookRegion? SelectedRegion
    {
        get => _selectedRegion;
        set
        {
            if (SetProperty(ref _selectedRegion, value))
            {
                UpdateDefaultOutputFolder();
                CheckExistingConfigs();
            }
        }
    }

    public string SelectedServerMode
    {
        get => _selectedServerMode;
        set => SetProperty(ref _selectedServerMode, value);
    }

    public PortOption? SelectedPort
    {
        get => _selectedPort;
        set => SetProperty(ref _selectedPort, value);
    }

    public int Quantity
    {
        get => _quantity;
        set
        {
            if (SetProperty(ref _quantity, Math.Max(1, value)))
            {
                CheckExistingConfigs();
            }
        }
    }

    public string OutputFolder
    {
        get => _outputFolder;
        set
        {
            if (SetProperty(ref _outputFolder, value))
            {
                CheckExistingConfigs();
            }
        }
    }

    public bool AcceptConditions
    {
        get => _acceptConditions;
        set => SetProperty(ref _acceptConditions, value);
    }

    public bool IsGenerating
    {
        get => _isGenerating;
        set
        {
            if (SetProperty(ref _isGenerating, value))
            {
                OnPropertyChanged(nameof(CanStart));
            }
        }
    }

    public bool IsPaused
    {
        get => _isPaused;
        set => SetProperty(ref _isPaused, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string ProgressText
    {
        get => _progressText;
        set => SetProperty(ref _progressText, value);
    }

    public int ProgressValue
    {
        get => _progressValue;
        set => SetProperty(ref _progressValue, value);
    }

    public int ProgressMax
    {
        get => _progressMax;
        set => SetProperty(ref _progressMax, value);
    }

    public bool ShowSummary
    {
        get => _showSummary;
        set => SetProperty(ref _showSummary, value);
    }

    public int SummaryRequested
    {
        get => _summaryRequested;
        set => SetProperty(ref _summaryRequested, value);
    }

    public int SummaryGenerated
    {
        get => _summaryGenerated;
        set => SetProperty(ref _summaryGenerated, value);
    }

    public int SummaryValid
    {
        get => _summaryValid;
        set => SetProperty(ref _summaryValid, value);
    }

    public int SummaryDuplicates
    {
        get => _summaryDuplicates;
        set => SetProperty(ref _summaryDuplicates, value);
    }

    public int SummaryFailed
    {
        get => _summaryFailed;
        set => SetProperty(ref _summaryFailed, value);
    }

    public string SummaryOutputPath
    {
        get => _summaryOutputPath;
        set => SetProperty(ref _summaryOutputPath, value);
    }

    public string ResumeRecommendationText
    {
        get => _resumeRecommendationText;
        set => SetProperty(ref _resumeRecommendationText, value);
    }

    public bool CanResume
    {
        get => _canResume;
        set => SetProperty(ref _canResume, value);
    }

    public bool CanStart => !IsGenerating;

    public CollectorViewModel(VpnBookAutomationService? automationService = null, ProfileStorageService? storageService = null)
    {
        _automationService = automationService ?? new VpnBookAutomationService();
        _storageService = storageService ?? new ProfileStorageService();

        // Populate defaults
        foreach (var r in VpnBookAutomationService.GetDefaultRegions())
        {
            AvailableRegions.Add(r);
        }

        foreach (var p in VpnBookAutomationService.GetDefaultPorts())
        {
            AvailablePorts.Add(p);
        }

        SelectedRegion = AvailableRegions.FirstOrDefault();
        SelectedPort = AvailablePorts.FirstOrDefault();

        // Multi-Region plan defaults
        MultiRegionPlan.Add(new MultiRegionPlanItem { RegionCode = "US", DisplayName = "United States", Quantity = 20 });
        MultiRegionPlan.Add(new MultiRegionPlanItem { RegionCode = "CA", DisplayName = "Canada", Quantity = 10 });
        MultiRegionPlan.Add(new MultiRegionPlanItem { RegionCode = "UK", DisplayName = "United Kingdom", Quantity = 15 });
        MultiRegionPlan.Add(new MultiRegionPlanItem { RegionCode = "DE", DisplayName = "Germany", Quantity = 10 });
        MultiRegionPlan.Add(new MultiRegionPlanItem { RegionCode = "FR", DisplayName = "France", Quantity = 10 });

        UpdateDefaultOutputFolder();
        CheckExistingConfigs();
    }

    private void UpdateDefaultOutputFolder()
    {
        if (SelectedRegion == null) return;
        var configsRoot = ProfileStorageService.GetDefaultConfigsRoot();
        OutputFolder = ProfileStorageService.GetRegionFolder(configsRoot, SelectedRegion.Code);
    }

    public void CheckExistingConfigs()
    {
        if (SelectedRegion == null || string.IsNullOrWhiteSpace(OutputFolder) || !Directory.Exists(OutputFolder))
        {
            CanResume = false;
            ResumeRecommendationText = string.Empty;
            return;
        }

        var existingCount = ProfileStorageService.GetExistingConfigCount(OutputFolder, SelectedRegion.Code);
        if (existingCount > 0 && existingCount < Quantity)
        {
            var remaining = Quantity - existingCount;
            CanResume = true;
            ResumeRecommendationText = $"Found {existingCount} existing profiles in target folder. You can resume remaining {remaining} profiles.";
        }
        else if (existingCount >= Quantity)
        {
            CanResume = false;
            ResumeRecommendationText = $"Folder already contains {existingCount} profiles. Next batch will automatically continue at index {existingCount + 1}.";
        }
        else
        {
            CanResume = false;
            ResumeRecommendationText = string.Empty;
        }
    }

    public void ResumeRemaining()
    {
        if (SelectedRegion == null || !Directory.Exists(OutputFolder)) return;
        var existingCount = ProfileStorageService.GetExistingConfigCount(OutputFolder, SelectedRegion.Code);
        if (existingCount > 0 && existingCount < Quantity)
        {
            Quantity -= existingCount;
            CheckExistingConfigs();
        }
    }

    public async Task StartBatchAsync()
    {
        if (!AcceptConditions)
        {
            StatusMessage = "Please confirm acceptance of provider conditions before starting.";
            return;
        }

        if (SelectedRegion == null)
        {
            StatusMessage = "Please select a region.";
            return;
        }

        if (SelectedPort == null)
        {
            StatusMessage = "Please select a port.";
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputFolder))
        {
            StatusMessage = "Please select an output folder.";
            return;
        }

        IsGenerating = true;
        IsPaused = false;
        ShowSummary = false;
        Results.Clear();

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var region = SelectedRegion;
        var port = SelectedPort.Port;
        var targetFolder = OutputFolder;
        var quantity = Quantity;

        ProgressMax = quantity;
        ProgressValue = 0;
        ProgressText = $"0 / {quantity} generated";
        StatusMessage = $"Starting generation of {quantity} profiles for {region.DisplayName}...";

        int generated = 0;
        int valid = 0;
        int duplicates = 0;
        int failed = 0;

        try
        {
            for (int i = 0; i < quantity; i++)
            {
                if (token.IsCancellationRequested) break;

                // Pick server based on mode
                var server = PickServer(region, i, SelectedServerMode);
                StatusMessage = $"[{i + 1}/{quantity}] Requesting configuration from {server.Name}...";

                var result = await _automationService.GenerateSingleProfileAsync(
                    server,
                    port,
                    status => StatusMessage = $"[{i + 1}/{quantity}] {status}",
                    token);

                if (result.RequiresOperatorAttention)
                {
                    IsPaused = true;
                    StatusMessage = result.ErrorMessage;
                    break;
                }

                if (result.Success && !string.IsNullOrWhiteSpace(result.ConfigContent))
                {
                    generated++;
                    var saveResult = _storageService.SaveValidatedProfile(
                        result.ConfigContent,
                        targetFolder,
                        region.Code,
                        server.Name,
                        result.ExpiresAtUtc);

                    if (saveResult.Success)
                    {
                        valid++;
                        ProgressValue = valid;
                        ProgressText = $"{valid} / {quantity} generated";

                        Results.Insert(0, new ProfileResultItem
                        {
                            Filename = saveResult.Filename,
                            Region = region.Code,
                            ServerName = server.Name,
                            Status = "Ready",
                            ExpiresAtUtc = result.ExpiresAtUtc,
                            DerivedPublicKeyHash = saveResult.IdentityHash
                        });

                        StatusMessage = $"Generated {saveResult.Filename} ({server.Name}) successfully.";
                    }
                    else if (saveResult.IsDuplicate)
                    {
                        duplicates++;
                        Results.Insert(0, new ProfileResultItem
                        {
                            Filename = $"[Duplicate] ({server.Name})",
                            Region = region.Code,
                            ServerName = server.Name,
                            Status = "Duplicate",
                            StatusDetail = saveResult.Message,
                            DerivedPublicKeyHash = saveResult.IdentityHash
                        });

                        StatusMessage = $"Duplicate profile identity detected. Quarantined. Continuing...";
                        // Duplicates do NOT count towards valid quantity, so decrement i to retry with next server
                        i--;
                    }
                    else
                    {
                        failed++;
                        Results.Insert(0, new ProfileResultItem
                        {
                            Filename = $"[Validation Failed]",
                            Region = region.Code,
                            ServerName = server.Name,
                            Status = "Failed",
                            StatusDetail = saveResult.Message
                        });
                        StatusMessage = $"Validation failed: {saveResult.Message}";
                    }
                }
                else
                {
                    failed++;
                    Results.Insert(0, new ProfileResultItem
                    {
                        Filename = $"[Generation Failed]",
                        Region = region.Code,
                        ServerName = server.Name,
                        Status = "Failed",
                        StatusDetail = result.ErrorMessage
                    });
                    StatusMessage = $"Generation failed for {server.Name}: {result.ErrorMessage}";
                }

                // Configurable respectful delay between generations (3-5 seconds)
                if (i < quantity - 1 && !token.IsCancellationRequested)
                {
                    StatusMessage = $"Waiting 4s before next generation to respect provider rate limits...";
                    await Task.Delay(4000, token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Batch generation cancelled by operator.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Batch stopped with error: {ex.Message}";
        }
        finally
        {
            IsGenerating = false;
            CheckExistingConfigs();

            // Set summary
            SummaryRequested = quantity;
            SummaryGenerated = generated;
            SummaryValid = valid;
            SummaryDuplicates = duplicates;
            SummaryFailed = failed;
            SummaryOutputPath = targetFolder;
            ShowSummary = true;
        }
    }

    public async Task StartMultiRegionBatchAsync()
    {
        if (!AcceptConditions)
        {
            StatusMessage = "Please confirm acceptance of provider conditions before starting.";
            return;
        }

        IsGenerating = true;
        IsPaused = false;
        ShowSummary = false;
        Results.Clear();

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var configsRoot = ProfileStorageService.GetDefaultConfigsRoot();
        var port = SelectedPort?.Port ?? "443";

        try
        {
            foreach (var item in MultiRegionPlan)
            {
                if (token.IsCancellationRequested) break;
                if (item.Quantity <= 0) continue;

                var region = AvailableRegions.FirstOrDefault(r => string.Equals(r.Code, item.RegionCode, StringComparison.OrdinalIgnoreCase));
                if (region == null) continue;

                var folder = ProfileStorageService.GetRegionFolder(configsRoot, region.Code);
                item.Status = "In Progress";

                ProgressMax = item.Quantity;
                ProgressValue = 0;
                ProgressText = $"0 / {item.Quantity} ({region.DisplayName})";
                StatusMessage = $"Starting sequential batch for {region.DisplayName} ({item.Quantity} profiles)...";

                int validForRegion = 0;

                for (int i = 0; i < item.Quantity; i++)
                {
                    if (token.IsCancellationRequested) break;

                    var server = PickServer(region, i, "Automatic");
                    var result = await _automationService.GenerateSingleProfileAsync(
                        server,
                        port,
                        msg => StatusMessage = $"[{region.Code} {i + 1}/{item.Quantity}] {msg}",
                        token);

                    if (result.RequiresOperatorAttention)
                    {
                        IsPaused = true;
                        StatusMessage = result.ErrorMessage;
                        item.Status = "Paused";
                        return;
                    }

                    if (result.Success && !string.IsNullOrWhiteSpace(result.ConfigContent))
                    {
                        var saveResult = _storageService.SaveValidatedProfile(
                            result.ConfigContent,
                            folder,
                            region.Code,
                            server.Name,
                            result.ExpiresAtUtc);

                        if (saveResult.Success)
                        {
                            validForRegion++;
                            ProgressValue = validForRegion;
                            ProgressText = $"{validForRegion} / {item.Quantity} ({region.DisplayName})";

                            Results.Insert(0, new ProfileResultItem
                            {
                                Filename = saveResult.Filename,
                                Region = region.Code,
                                ServerName = server.Name,
                                Status = "Ready",
                                ExpiresAtUtc = result.ExpiresAtUtc,
                                DerivedPublicKeyHash = saveResult.IdentityHash
                            });
                        }
                        else if (saveResult.IsDuplicate)
                        {
                            i--; // retry with next server
                        }
                    }

                    if (i < item.Quantity - 1 && !token.IsCancellationRequested)
                    {
                        await Task.Delay(4000, token);
                    }
                }

                item.Status = $"Done ({validForRegion}/{item.Quantity})";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Multi-region batch cancelled by operator.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Multi-region batch stopped: {ex.Message}";
        }
        finally
        {
            IsGenerating = false;
            CheckExistingConfigs();
            ShowSummary = true;
            SummaryOutputPath = configsRoot;
        }
    }

    public void CancelBatch()
    {
        _cts?.Cancel();
        StatusMessage = "Cancelling batch generation...";
    }

    private VpnBookServer PickServer(VpnBookRegion region, int iterationIndex, string mode)
    {
        if (region.Servers.Count == 0)
        {
            return new VpnBookServer { Id = "us16", Name = "Server 1", Hostname = "us16.vpnbook.com", CountryCode = region.Code, CountryName = region.DisplayName };
        }

        if (mode.Contains("1") && region.Servers.Count > 0)
        {
            return region.Servers[0];
        }

        if (mode.Contains("2") && region.Servers.Count > 1)
        {
            return region.Servers[1];
        }

        // Automatic: alternate across available servers
        return region.Servers[iterationIndex % region.Servers.Count];
    }

    public void OpenOutputFolder()
    {
        try
        {
            var target = !string.IsNullOrWhiteSpace(SummaryOutputPath) && Directory.Exists(SummaryOutputPath)
                ? SummaryOutputPath
                : OutputFolder;

            if (Directory.Exists(target))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = target,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to open folder: {ex.Message}";
        }
    }

    public void ImportIntoMaintainer()
    {
        try
        {
            // Find Maintainer executable
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var maintainerPath = Path.Combine(baseDir, "DCSS.Maintainer.exe");

            if (!File.Exists(maintainerPath))
            {
                var parent = Directory.GetParent(baseDir);
                while (parent != null)
                {
                    var check = Path.Combine(parent.FullName, "dist", "maintainer", "DCSS.Maintainer.exe");
                    if (File.Exists(check))
                    {
                        maintainerPath = check;
                        break;
                    }
                    var check2 = Path.Combine(parent.FullName, "tools", "DCSS.Maintainer", "bin", "Debug", "net8.0-windows", "DCSS.Maintainer.exe");
                    if (File.Exists(check2))
                    {
                        maintainerPath = check2;
                        break;
                    }
                    parent = parent.Parent;
                }
            }

            if (File.Exists(maintainerPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = maintainerPath,
                    UseShellExecute = true
                });
                StatusMessage = "DCSS Maintainer launched. You can import the generated profile configs.";
            }
            else
            {
                OpenOutputFolder();
                StatusMessage = "Profile configs folder opened. Import into Maintainer.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to launch Maintainer: {ex.Message}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
