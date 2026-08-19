using System.Windows;
using System.Windows.Threading;

namespace DCSS.Maintainer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Fatal memory or runtime errors must terminate safely
        if (e.Exception is OutOfMemoryException or StackOverflowException)
        {
            e.Handled = false;
            return;
        }

        // Recoverable UI/COM/IO exceptions
        e.Handled = true;
        MessageBox.Show(
            $"An unexpected UI operation error occurred:\n{e.Exception.Message}\n\nThe application will remain open safely.",
            "DCSS.Maintainer Warning",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            MessageBox.Show(
                $"A critical system error occurred:\n{ex.Message}",
                "DCSS.Maintainer Fatal Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
    }
}

