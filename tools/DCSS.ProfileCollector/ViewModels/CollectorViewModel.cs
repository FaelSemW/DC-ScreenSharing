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
    private readonly ProtonVpnAutomationService _protonService;
    private readonly VpnBookAutomationService _vpnBookService;
    private readonly ProfileStorageService _storageService;
    private CancellationTokenSource? _cts;

    private string _selectedProvider = ProviderConstants.ProtonVpn;
    private CollectorRegion? _selectedRegion;
    private string _selectedServerMode = "Automatic";
    private PortOption? _selectedPort;
    private int _quantity = 5;
    private string _outputFolder = string.Empty;
    private bool _acceptConditions = true;
    private bool _isGenerating = false;
    private bool _isPaused = false;
    private bool _needsOperatorAttention = false;
    private string _operatorAttentionMessage = string.Empty;
    private string _statusMessage = "Ready";
    private string _progressText = "0 / 0 generated";
    private int _progressValue = 0;
    private int _progressMax = 5;
    private bool _showSummary = false;
    private int _summaryRequested;
    private int _summaryGenerated;
    private int _summaryValid;
    private int _summaryDuplicates;
    private int _summaryFailed;
    private string _summaryOutputPath = string.Empty;
    private string _resumeRecommendationText = string.Empty;
    private bool _canResume = false;

    private ProtonOptions _protonSettings = new();

    public ObservableCollection<string> AvailableProviders { get; } = new() { ProviderConstants.ProtonVpn, ProviderConstants.VpnBook };
    public ObservableCollection<CollectorRegion> AvailableRegions { get; } = new();
    public ObservableCollection<string> ServerModes { get; } = new() { "Automatic", "Recommended", "Server 1", "Server 2" };
    public ObservableCollection<PortOption> AvailablePorts { get; } = new();
    public ObservableCollection<ProfileResultItem> Results { get; } = new();
    public ObservableCollection<MultiRegionPlanItem> MultiRegionPlan { get; } = new();

    public string SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (SetProperty(ref _selectedProvider, value))
            {
                OnPropertyChanged(nameof(IsProtonSelected));
                OnPropertyChanged(nameof(IsVpnBookSelected));
                UpdateRegionsForProvider();
                UpdateDefaultOutputFolder();
                CheckExistingConfigs();
            }
        }
    }

    public bool IsProtonSelected => string.Equals(SelectedProvider, ProviderConstants.ProtonVpn, StringComparison.OrdinalIgnoreCase);
    public bool IsVpnBookSelected => string.Equals(SelectedProvider, ProviderConstants.VpnBook, StringComparison.OrdinalIgnoreCase);

    public ProtonOptions ProtonSettings
    {
        get => _protonSettings;
        set => SetProperty(ref _protonSettings, value);
    }

    public CollectorRegion? SelectedRegion
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

    public bool NeedsOperatorAttention
    {
        get => _needsOperatorAttention;
        set => SetProperty(ref _needsOperatorAttention, value);
    }

    public string OperatorAttentionMessage
    {
        get => _operatorAttentionMessage;
        set => SetProperty(ref _operatorAttentionMessage, value);
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

    public CollectorViewModel(
        ProtonVpnAutomationService? protonService = null,
        VpnBookAutomationService? vpnBookService = null,
        ProfileStorageService? storageService = null)
    {
        _protonService = protonService ?? new ProtonVpnAutomationService();
        _vpnBookService = vpnBookService ?? new VpnBookAutomationService();
        _storageService = storageService ?? new ProfileStorageService();

        UpdateRegionsForProvider();

        foreach (var p in VpnBookAutomationService.GetDefaultPorts())
        {
            AvailablePorts.Add(p);
        }

        SelectedPort = AvailablePorts.FirstOrDefault();

        // Multi-Region plan defaults
        MultiRegionPlan.Add(new MultiRegionPlanItem { RegionCode = "US", DisplayName = "United States", Quantity = 5 });
        MultiRegionPlan.Add(new MultiRegionPlanItem { RegionCode = "CA", DisplayName = "Canada", Quantity = 3 });
        MultiRegionPlan.Add(new MultiRegionPlanItem { RegionCode = "UK", DisplayName = "United Kingdom", Quantity = 3 });
        MultiRegionPlan.Add(new MultiRegionPlanItem { RegionCode = "NL", DisplayName = "Netherlands", Quantity = 3 });
        MultiRegionPlan.Add(new MultiRegionPlanItem { RegionCode = "DE", DisplayName = "Germany", Quantity = 3 });

        UpdateDefaultOutputFolder();
        CheckExistingConfigs();
    }

    private void UpdateRegionsForProvider()
    {
        AvailableRegions.Clear();
        if (IsProtonSelected)
        {
            foreach (var r in ProtonVpnAutomationService.GetDefaultRegions())
            {
                AvailableRegions.Add(r);
            }
        }
        else
        {
            foreach (var r in VpnBookAutomationService.GetDefaultRegions())
            {
                AvailableRegions.Add(r);
            }
        }

        SelectedRegion = AvailableRegions.FirstOrDefault();
    }

    private void UpdateDefaultOutputFolder()
    {
        if (SelectedRegion == null) return;
        var configsRoot = ProfileStorageService.GetDefaultConfigsRoot();
        OutputFolder = ProfileStorageService.GetRegionFolder(configsRoot, SelectedRegion.Code, SelectedProvider);
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

    public async Task GenerateTestProfileAsync()
    {
        // Performance diagnostic mode: generate exactly 1 profile
        var origQuantity = Quantity;
        Quantity = 1;
        try
        {
            await StartBatchAsync();
        }
        finally
        {
            Quantity = origQuantity;
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

        if (string.IsNullOrWhiteSpace(OutputFolder))
        {
            StatusMessage = "Please select an output folder.";
            return;
        }

        IsGenerating = true;
        IsPaused = false;
        NeedsOperatorAttention = false;
        ShowSummary = false;
        Results.Clear();

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var region = SelectedRegion;
        var port = SelectedPort?.Port ?? "51820";
        var targetFolder = OutputFolder;
        var quantity = Quantity;
        var provider = SelectedProvider;

        ProgressMax = quantity;
        ProgressValue = 0;
        ProgressText = $"0 / {quantity} generated";
        StatusMessage = $"Starting generation of {quantity} profiles ({provider} - {region.DisplayName})...";

        int generated = 0;
        int valid = 0;
        int duplicates = 0;
        int failed = 0;

        try
        {
            for (int i = 0; i < quantity; i++)
            {
                if (token.IsCancellationRequested) break;

                var configName = $"DCSS-{region.Code}-{i + 1:D3}";
                StatusMessage = $"[{i + 1}/{quantity}] Requesting configuration {configName}...";

                ProviderProfileResult result;

                if (string.Equals(provider, ProviderConstants.ProtonVpn, StringComparison.OrdinalIgnoreCase))
                {
                    var options = new ProfileGenerationOptions
                    {
                        Provider = ProviderConstants.ProtonVpn,
                        Region = region,
                        ServerMode = SelectedServerMode,
                        Port = port,
                        ConfigurationName = configName,
                        ProtonSettings = ProtonSettings
                    };

                    result = await _protonService.GenerateSingleProfileAsync(
                        options,
                        msg => StatusMessage = $"[{i + 1}/{quantity}] {msg}",
                        token);
                }
                else
                {
                    var vpnBookServer = PickVpnBookServer(region, i, SelectedServerMode);
                    var vpnBookResult = await _vpnBookService.GenerateSingleProfileAsync(
                        vpnBookServer,
                        port,
                        msg => StatusMessage = $"[{i + 1}/{quantity}] {msg}",
                        token);

                    result = new ProviderProfileResult
                    {
                        Success = vpnBookResult.Success,
                        ConfigContent = vpnBookResult.ConfigContent,
                        ServerName = vpnBookResult.ServerName,
                        ExpiresAtUtc = vpnBookResult.ExpiresAtUtc,
                        ErrorMessage = vpnBookResult.ErrorMessage,
                        RequiresOperatorAttention = vpnBookResult.RequiresOperatorAttention,
                        OperatorAttentionReason = vpnBookResult.ErrorMessage
                    };
                }

                if (result.RequiresOperatorAttention)
                {
                    IsPaused = true;
                    NeedsOperatorAttention = true;
                    OperatorAttentionMessage = result.OperatorAttentionReason;
                    StatusMessage = result.OperatorAttentionReason;
                    break;
                }

                if (result.Success && !string.IsNullOrWhiteSpace(result.ConfigContent))
                {
                    generated++;
                    var saveResult = _storageService.SaveValidatedProfile(
                        result.ConfigContent,
                        targetFolder,
                        region.Code,
                        result.ServerName,
                        result.ExpiresAtUtc,
                        provider);

                    if (saveResult.Success)
                    {
                        valid++;
                        ProgressValue = valid;
                        ProgressText = $"{valid} / {quantity} generated";

                        Results.Insert(0, new ProfileResultItem
                        {
                            Filename = saveResult.Filename,
                            Provider = provider,
                            Region = region.Code,
                            ServerName = result.ServerName,
                            Status = "Ready",
                            ExpiresAtUtc = result.ExpiresAtUtc,
                            DerivedPublicKeyHash = saveResult.IdentityHash
                        });

                        StatusMessage = $"Generated {saveResult.Filename} ({result.ServerName}) successfully (sing-box validated).";
                    }
                    else if (saveResult.IsDuplicate)
                    {
                        duplicates++;
                        Results.Insert(0, new ProfileResultItem
                        {
                            Filename = $"[Duplicate] ({result.ServerName})",
                            Provider = provider,
                            Region = region.Code,
                            ServerName = result.ServerName,
                            Status = "Duplicate",
                            StatusDetail = saveResult.Message,
                            DerivedPublicKeyHash = saveResult.IdentityHash
                        });

                        StatusMessage = $"Duplicate profile identity detected. Quarantined. Continuing...";
                        i--; // Retrying slot
                    }
                    else
                    {
                        failed++;
                        Results.Insert(0, new ProfileResultItem
                        {
                            Filename = $"[Validation Failed]",
                            Provider = provider,
                            Region = region.Code,
                            ServerName = result.ServerName,
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
                        Provider = provider,
                        Region = region.Code,
                        ServerName = result.ServerName,
                        Status = "Failed",
                        StatusDetail = result.ErrorMessage
                    });
                    StatusMessage = $"Generation failed: {result.ErrorMessage}";
                }

                // Respectful pause between creations (3-5s)
                if (i < quantity - 1 && !token.IsCancellationRequested)
                {
                    StatusMessage = $"Waiting 4s before next generation...";
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
        NeedsOperatorAttention = false;
        ShowSummary = false;
        Results.Clear();

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var configsRoot = ProfileStorageService.GetDefaultConfigsRoot();
        var port = SelectedPort?.Port ?? "51820";
        var provider = SelectedProvider;

        try
        {
            foreach (var item in MultiRegionPlan)
            {
                if (token.IsCancellationRequested) break;
                if (item.Quantity <= 0) continue;

                var region = AvailableRegions.FirstOrDefault(r => string.Equals(r.Code, item.RegionCode, StringComparison.OrdinalIgnoreCase));
                if (region == null) continue;

                var folder = ProfileStorageService.GetRegionFolder(configsRoot, region.Code, provider);
                item.Status = "In Progress";

                ProgressMax = item.Quantity;
                ProgressValue = 0;
                ProgressText = $"0 / {item.Quantity} ({region.DisplayName})";
                StatusMessage = $"Starting sequential batch for {region.DisplayName} ({item.Quantity} profiles)...";

                int validForRegion = 0;

                for (int i = 0; i < item.Quantity; i++)
                {
                    if (token.IsCancellationRequested) break;

                    var configName = $"DCSS-{region.Code}-{i + 1:D3}";
                    ProviderProfileResult result;

                    if (string.Equals(provider, ProviderConstants.ProtonVpn, StringComparison.OrdinalIgnoreCase))
                    {
                        var options = new ProfileGenerationOptions
                        {
                            Provider = ProviderConstants.ProtonVpn,
                            Region = region,
                            ServerMode = "Automatic",
                            Port = port,
                            ConfigurationName = configName,
                            ProtonSettings = ProtonSettings
                        };

                        result = await _protonService.GenerateSingleProfileAsync(
                            options,
                            msg => StatusMessage = $"[{region.Code} {i + 1}/{item.Quantity}] {msg}",
                            token);
                    }
                    else
                    {
                        var vpnBookServer = PickVpnBookServer(region, i, "Automatic");
                        var vpnBookResult = await _vpnBookService.GenerateSingleProfileAsync(
                            vpnBookServer,
                            port,
                            msg => StatusMessage = $"[{region.Code} {i + 1}/{item.Quantity}] {msg}",
                            token);

                        result = new ProviderProfileResult
                        {
                            Success = vpnBookResult.Success,
                            ConfigContent = vpnBookResult.ConfigContent,
                            ServerName = vpnBookResult.ServerName,
                            ExpiresAtUtc = vpnBookResult.ExpiresAtUtc,
                            ErrorMessage = vpnBookResult.ErrorMessage,
                            RequiresOperatorAttention = vpnBookResult.RequiresOperatorAttention,
                            OperatorAttentionReason = vpnBookResult.ErrorMessage
                        };
                    }

                    if (result.RequiresOperatorAttention)
                    {
                        IsPaused = true;
                        NeedsOperatorAttention = true;
                        OperatorAttentionMessage = result.OperatorAttentionReason;
                        item.Status = "Attention Required";
                        break;
                    }

                    if (result.Success && !string.IsNullOrWhiteSpace(result.ConfigContent))
                    {
                        var saveResult = _storageService.SaveValidatedProfile(
                            result.ConfigContent,
                            folder,
                            region.Code,
                            result.ServerName,
                            result.ExpiresAtUtc,
                            provider);

                        if (saveResult.Success)
                        {
                            validForRegion++;
                            ProgressValue = validForRegion;
                            ProgressText = $"{validForRegion} / {item.Quantity} ({region.DisplayName})";

                            Results.Insert(0, new ProfileResultItem
                            {
                                Filename = saveResult.Filename,
                                Provider = provider,
                                Region = region.Code,
                                ServerName = result.ServerName,
                                Status = "Ready",
                                ExpiresAtUtc = result.ExpiresAtUtc,
                                DerivedPublicKeyHash = saveResult.IdentityHash
                            });
                        }
                    }

                    if (i < item.Quantity - 1 && !token.IsCancellationRequested)
                    {
                        await Task.Delay(4000, token);
                    }
                }

                item.Status = validForRegion >= item.Quantity ? "Completed" : $"Partial ({validForRegion}/{item.Quantity})";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Multi-region job cancelled.";
        }
        finally
        {
            IsGenerating = false;
        }
    }

    public async Task ContinueAfterAttentionAsync()
    {
        NeedsOperatorAttention = false;
        IsPaused = false;
        StatusMessage = "Resuming generation...";
        await StartBatchAsync();
    }

    public void CancelBatch()
    {
        _cts?.Cancel();
        StatusMessage = "Cancelling current batch operation...";
    }

    public void OpenOutputFolder()
    {
        if (!string.IsNullOrWhiteSpace(OutputFolder) && Directory.Exists(OutputFolder))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = OutputFolder,
                UseShellExecute = true
            });
        }
    }

    public void ImportIntoMaintainer()
    {
        try
        {
            // Launch or notify DCSS.Maintainer
            var maintainerExe = FindMaintainerExecutable();
            if (File.Exists(maintainerExe))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = maintainerExe,
                    Arguments = $"--import-dir \"{OutputFolder}\"",
                    UseShellExecute = true
                });
                StatusMessage = "Opened Maintainer to import generated profiles.";
            }
            else
            {
                StatusMessage = $"Profiles ready at {OutputFolder}. Open DCSS.Maintainer to import.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Import action note: {ex.Message}";
        }
    }

    private string FindMaintainerExecutable()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "DCSS.Maintainer.exe"),
            Path.Combine(baseDir, "..", "DCSS.Maintainer", "DCSS.Maintainer.exe"),
            @"D:\DC-ScreenSharing\tools\DCSS.Maintainer\bin\Debug\net8.0-windows\DCSS.Maintainer.exe"
        };
        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }

    private static VpnBookServer PickVpnBookServer(CollectorRegion region, int index, string mode)
    {
        if (region.Servers == null || region.Servers.Count == 0)
        {
            return new VpnBookServer { Id = $"{region.Code.ToLowerInvariant()}1", Name = $"{region.DisplayName} Server", CountryCode = region.Code, CountryName = region.DisplayName };
        }

        if (string.Equals(mode, "Server 1", StringComparison.OrdinalIgnoreCase))
        {
            return (VpnBookServer)region.Servers[0];
        }

        if (string.Equals(mode, "Server 2", StringComparison.OrdinalIgnoreCase) && region.Servers.Count > 1)
        {
            return (VpnBookServer)region.Servers[1];
        }

        var serverIndex = index % region.Servers.Count;
        return (VpnBookServer)region.Servers[serverIndex];
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;
        storage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
