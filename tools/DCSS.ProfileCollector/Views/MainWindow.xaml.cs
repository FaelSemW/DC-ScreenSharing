using System.Windows;
using DCSS.ProfileCollector.ViewModels;
using Microsoft.Win32;

namespace DCSS.ProfileCollector.Views;

public partial class MainWindow : Window
{
    private readonly CollectorViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new CollectorViewModel();
        DataContext = _viewModel;
    }

    private void OnBrowseOutputFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Output Folder for WireGuard Profiles",
            InitialDirectory = _viewModel.OutputFolder
        };

        if (dialog.ShowDialog() == true)
        {
            _viewModel.OutputFolder = dialog.FolderName;
        }
    }

    private void OnResumeRemainingClick(object sender, RoutedEventArgs e)
    {
        _viewModel.ResumeRemaining();
    }

    private async void OnGenerateProfilesClick(object sender, RoutedEventArgs e)
    {
        await _viewModel.StartBatchAsync();
    }

    private async void OnGenerateAllRegionsClick(object sender, RoutedEventArgs e)
    {
        await _viewModel.StartMultiRegionBatchAsync();
    }

    private void OnCancelBatchClick(object sender, RoutedEventArgs e)
    {
        _viewModel.CancelBatch();
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenOutputFolder();
    }

    private void OnImportIntoMaintainerClick(object sender, RoutedEventArgs e)
    {
        _viewModel.ImportIntoMaintainer();
    }
}
