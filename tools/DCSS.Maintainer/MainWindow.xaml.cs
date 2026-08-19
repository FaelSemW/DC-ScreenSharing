using System.IO;
using System.Windows;
using DCSS.Maintainer.ViewModels;
using Microsoft.Win32;

namespace DCSS.Maintainer;

public partial class MainWindow : Window
{
    private readonly MaintainerViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MaintainerViewModel();
        DataContext = _viewModel;
    }

    private void OnAddServerClick(object sender, RoutedEventArgs e)
    {
        var newServer = new MaintainerServerItem
        {
            Id = $"server-{_viewModel.Servers.Count + 1:D2}",
            Name = "New Server Location",
            Region = "US",
            Endpoint = "vpn.example.com",
            Port = 51820,
            Address = "10.8.0.2/32"
        };
        _viewModel.Servers.Add(newServer);
        _viewModel.SelectedServer = newServer;
    }

    private void OnImportNewConfClick(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Title = "Select WireGuard Configuration File",
            Filter = "WireGuard Config (*.conf)|*.conf|All Files (*.*)|*.*"
        };

        if (ofd.ShowDialog() == true)
        {
            try
            {
                var content = File.ReadAllText(ofd.FileName);
                var baseName = Path.GetFileNameWithoutExtension(ofd.FileName);
                var newServer = new MaintainerServerItem
                {
                    Id = baseName.ToLowerInvariant().Replace(" ", "-"),
                    Name = baseName,
                    Region = "US"
                };

                _viewModel.ImportConfIntoServer(newServer, content);
                _viewModel.Servers.Add(newServer);
                _viewModel.SelectedServer = newServer;

                MessageBox.Show($"Server '{newServer.Name}' added from WireGuard .conf successfully!", "Import Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to parse WireGuard .conf:\n{ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void OnReplaceConfClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer == null)
        {
            MessageBox.Show("Please select a server first.", "Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var ofd = new OpenFileDialog
        {
            Title = $"Select New WireGuard Config for '{_viewModel.SelectedServer.Name}'",
            Filter = "WireGuard Config (*.conf)|*.conf|All Files (*.*)|*.*"
        };

        if (ofd.ShowDialog() == true)
        {
            try
            {
                var content = File.ReadAllText(ofd.FileName);
                _viewModel.ImportConfIntoServer(_viewModel.SelectedServer, content);
                MessageBox.Show($"Profile updated for server '{_viewModel.SelectedServer.Name}' from .conf!", "Update Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to parse WireGuard .conf:\n{ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void OnRemoveServerClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer != null)
        {
            _viewModel.Servers.Remove(_viewModel.SelectedServer);
            _viewModel.SelectedServer = _viewModel.Servers.FirstOrDefault();
        }
    }

    private async void OnCreateTicketClick(object sender, RoutedEventArgs e)
    {
        var (success, ticket, message) = await _viewModel.GenerateEnrollmentTicketAsync(validityMinutes: 30);
        if (success)
        {
            Clipboard.SetText(ticket);
            MessageBox.Show(
                $"Single-Use Client Enrollment Ticket:\n\n{ticket}\n\n• Validity: 30 minutes\n• Single Use: Automatically consumed upon client enrollment\n\n(Ticket copied to clipboard!)",
                "Enrollment Ticket Created",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(message, "Failed to Generate Ticket", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnKeyManagerClick(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Current Signing Public Key is active and saved in AppData (DPAPI protected).\n\nDo you want to regenerate a new signing key pair? (WARNING: Existing clients will reject publications signed with new keys unless the new public key is deployed to them!)",
            "Cryptographic Signing Key Manager",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            _viewModel.GenerateAndSaveNewKeys();
            MessageBox.Show("New RSA 2048-bit key pair generated and securely saved with DPAPI encryption.", "Keys Updated", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void OnPublishToServiceClick(object sender, RoutedEventArgs e)
    {
        var validation = _viewModel.ValidateAll();
        if (!validation.Success)
        {
            MessageBox.Show($"Validation failed:\n{validation.Message}", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var (success, message) = await _viewModel.PublishToServiceAsync();
        if (success)
        {
            MessageBox.Show(message, "Published", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(message, "Publication Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnRollbackClick(object sender, RoutedEventArgs e)
    {
        var prevGen = _viewModel.Generation - 2;
        if (prevGen <= 0) prevGen = 1;

        var result = MessageBox.Show(
            $"Are you sure you want to roll back the remote ProfileService to generation {prevGen}?",
            "Confirm Rollback",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            var (success, message) = await _viewModel.RollbackServiceAsync(prevGen);
            if (success)
            {
                MessageBox.Show(message, "Rollback Successful", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(message, "Rollback Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}