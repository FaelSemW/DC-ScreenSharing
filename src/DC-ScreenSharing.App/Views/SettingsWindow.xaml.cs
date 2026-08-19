using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DCScreenSharing.Core.Discord;
using DCScreenSharing.Core.Settings;
using Microsoft.Win32;

namespace DCScreenSharing.App.Views;

public partial class SettingsWindow : Window
{
    private readonly UserSettings _settings;
    private bool _isInitializing = true;

    public SettingsWindow()
    {
        InitializeComponent();

        _settings = App.SettingsManager.Load();

        ComboFlavor.ItemsSource = Enum.GetValues(typeof(DiscordFlavor));
        ComboFlavor.SelectedItem = _settings.PreferredFlavor;

        CheckAutoLaunch.IsChecked = _settings.AutoLaunchDiscord;
        CheckStartMinimized.IsChecked = _settings.StartMinimized;

        _isInitializing = false;
    }

    private void OnFlavorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        if (ComboFlavor.SelectedItem is DiscordFlavor flavor)
        {
            _settings.PreferredFlavor = flavor;
            SaveSettings();
            App.ViewModel.RefreshDiscord();
        }
    }

    private void OnSettingChecked(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        _settings.AutoLaunchDiscord = CheckAutoLaunch.IsChecked == true;
        _settings.StartMinimized = CheckStartMinimized.IsChecked == true;
        SaveSettings();
    }

    private void SaveSettings()
    {
        App.SettingsManager.Save(_settings);
    }

    private void OnOpenLogsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DC-ScreenSharing", "logs");
            Directory.CreateDirectory(logDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = logDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open logs folder: {ex.Message}", "Logs", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnExportDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        var sfd = new SaveFileDialog
        {
            Title = "Save Diagnostics Bundle",
            Filter = "Zip Archive (*.zip)|*.zip",
            FileName = $"DCSS_Diagnostics_{DateTime.UtcNow:yyyyMMdd_HHmmss}.zip"
        };

        if (sfd.ShowDialog() == true)
        {
            var exported = await App.DiagnosticsService.ExportDiagnosticsZipAsync(sfd.FileName);
            if (!string.IsNullOrEmpty(exported))
            {
                MessageBox.Show("Diagnostics bundle exported successfully.", "Diagnostics", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("Failed to export diagnostics. Please check logs.", "Diagnostics", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
