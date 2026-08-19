using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using DCSS.Maintainer.ViewModels;
using Microsoft.Win32;

namespace DCSS.Maintainer;

public partial class MainWindow : Window
{
    private readonly MaintainerViewModel _viewModel;
    private bool _isUpdatingPassword;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MaintainerViewModel();
        DataContext = _viewModel;

        // Initialize API key masking
        UpdateKeyMaskingState(masked: true);
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.RefreshActiveGenerationAsync();
        }
        catch (Exception ex)
        {
            _viewModel.StatusMessage = $"Initial sync notice: {ex.Message}";
        }
    }

    private void UpdateKeyMaskingState(bool masked)
    {
        try
        {
            _viewModel.ShowAdminApiKey = !masked;
            if (masked)
            {
                _isUpdatingPassword = true;
                AdminApiKeyPasswordBox.Password = _viewModel.AdminApiKey;
                _isUpdatingPassword = false;

                AdminApiKeyTextBox.Visibility = Visibility.Collapsed;
                AdminApiKeyPasswordBox.Visibility = Visibility.Visible;
                ToggleKeyVisibilityBtn.Content = "👁";
                ToggleKeyVisibilityBtn.ToolTip = "Show Admin API Key";
            }
            else
            {
                AdminApiKeyPasswordBox.Visibility = Visibility.Collapsed;
                AdminApiKeyTextBox.Visibility = Visibility.Visible;
                ToggleKeyVisibilityBtn.Content = "🔒";
                ToggleKeyVisibilityBtn.ToolTip = "Hide Admin API Key";
            }
        }
        catch (Exception ex)
        {
            _viewModel.StatusMessage = $"Masking state error: {ex.Message}";
        }
    }

    private void OnToggleKeyVisibilityClick(object sender, RoutedEventArgs e)
    {
        UpdateKeyMaskingState(masked: _viewModel.ShowAdminApiKey);
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingPassword) return;
        _viewModel.AdminApiKey = AdminApiKeyPasswordBox.Password;
    }

    private async void OnSyncRemoteClick(object sender, RoutedEventArgs e)
    {
        if (SyncRemoteBtn != null) SyncRemoteBtn.IsEnabled = false;
        try
        {
            var (success, activeGen, msg) = await _viewModel.RefreshActiveGenerationAsync();
            if (!success)
            {
                MessageBox.Show(msg, "Remote Sync", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to synchronize remote state:\n{ex.Message}", "Remote Sync Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (SyncRemoteBtn != null) SyncRemoteBtn.IsEnabled = true;
        }
    }

    private void OnAddServerClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var newServer = new MaintainerServerItem
            {
                Id = $"server-{_viewModel.Servers.Count + 1:D2}",
                Name = "New Server Location",
                Region = "US",
                Endpoint = "vpn.example.com",
                Port = 51820,
                Address = "10.8.0.2/32",
                Status = "Manual Entry (Private Key required)"
            };
            _viewModel.Servers.Add(newServer);
            _viewModel.SelectedServer = newServer;
            _viewModel.SaveSettings();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error adding server: {ex.Message}", "Server Management", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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

                _viewModel.ImportConfIntoServer(newServer, content, ofd.FileName);
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
                _viewModel.ImportConfIntoServer(_viewModel.SelectedServer, content, ofd.FileName);
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
        try
        {
            if (_viewModel.SelectedServer != null)
            {
                _viewModel.Servers.Remove(_viewModel.SelectedServer);
                _viewModel.SelectedServer = _viewModel.Servers.FirstOrDefault();
                _viewModel.SaveSettings();
                _viewModel.SaveSecrets();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error removing server: {ex.Message}", "Server Management", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public static bool TrySetClipboardText(string text, int retries = 5, int delayMs = 100)
    {
        if (string.IsNullOrEmpty(text)) return false;

        for (int i = 0; i < retries; i++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(delayMs);
            }
            catch (Exception)
            {
                break;
            }
        }
        return false;
    }

    private async void OnCreateTicketClick(object sender, RoutedEventArgs e)
    {
        if (CreateTicketBtn != null) CreateTicketBtn.IsEnabled = false;

        try
        {
            if (string.IsNullOrWhiteSpace(_viewModel.AdminApiKey))
            {
                MessageBox.Show("Please enter the Admin API Key first.", "Admin Key Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _viewModel.StatusMessage = "Requesting single-use enrollment ticket from ProfileService...";
            var (success, ticket, message) = await _viewModel.GenerateEnrollmentTicketAsync(validityMinutes: 30);
            
            if (success && !string.IsNullOrEmpty(ticket))
            {
                var copied = TrySetClipboardText(ticket);
                var clipboardNote = copied 
                    ? "(Ticket automatically copied to clipboard!)" 
                    : "(Note: Could not copy automatically to clipboard — please copy the code manually from above)";

                MessageBox.Show(
                    $"Single-Use Client Enrollment Ticket:\n\n{ticket}\n\n• Validity: 30 minutes\n• Single Use: Automatically consumed upon client enrollment\n\n{clipboardNote}",
                    "Enrollment Ticket Created",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(
                    string.IsNullOrWhiteSpace(message) ? "Unable to create activation code. Please check your Admin API Key and ProfileService connection." : message,
                    "Failed to Generate Ticket",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Unable to create activation code: {ex.Message}",
                "Activation Code Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            if (CreateTicketBtn != null) CreateTicketBtn.IsEnabled = true;
        }
    }

    private void OnKeyManagerClick(object sender, RoutedEventArgs e)
    {
        if (KeyManagerBtn != null) KeyManagerBtn.IsEnabled = false;

        try
        {
            var result = MessageBox.Show(
                "Cryptographic Signing & Credentials Security Manager:\n\n" +
                "• Signing Key: RSA 2048-bit (DPAPI protected)\n" +
                "• Admin API Key & Profile Secrets: DPAPI CurrentUser encrypted\n\n" +
                "Would you like to manage credentials?\n\n" +
                "Click [Yes] to Clear Saved Admin Credentials.\n" +
                "Click [No] to Regenerate RSA Signing Keys.\n" +
                "Click [Cancel] to Exit.",
                "Security & Credentials Manager",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _viewModel.ClearSavedAdminCredentials();
                UpdateKeyMaskingState(masked: true);
                MessageBox.Show("Saved Admin API Key and encrypted credentials cleared from disk.", "Credentials Cleared", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (result == MessageBoxResult.No)
            {
                var confirmRegen = MessageBox.Show(
                    "WARNING: Regenerating signing keys will cause existing client installations to reject updates unless the new public key is distributed to them.\n\nAre you sure you want to regenerate new signing keys?",
                    "Confirm Key Regeneration",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (confirmRegen == MessageBoxResult.Yes)
                {
                    _viewModel.GenerateAndSaveNewKeys();
                    MessageBox.Show("New RSA 2048-bit key pair generated and securely saved with DPAPI encryption.", "Keys Updated", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Unable to manage keys/credentials: {ex.Message}", "Security Manager Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (KeyManagerBtn != null) KeyManagerBtn.IsEnabled = true;
        }
    }

    private async void OnPublishToServiceClick(object sender, RoutedEventArgs e)
    {
        if (PublishBtn != null) PublishBtn.IsEnabled = false;

        try
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
        catch (Exception ex)
        {
            MessageBox.Show($"Unable to publish profile catalog: {ex.Message}", "Publication Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (PublishBtn != null) PublishBtn.IsEnabled = true;
        }
    }

    private async void OnRollbackClick(object sender, RoutedEventArgs e)
    {
        if (RollbackBtn != null) RollbackBtn.IsEnabled = false;

        try
        {
            var prevGen = _viewModel.ActiveGeneration > 1 ? _viewModel.ActiveGeneration - 1 : 1;

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
        catch (Exception ex)
        {
            MessageBox.Show($"Unable to execute rollback: {ex.Message}", "Rollback Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (RollbackBtn != null) RollbackBtn.IsEnabled = true;
        }
    }
}