using System.Windows;
using System.Windows.Threading;
using GhostServer.Services;

namespace GhostServer;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private static void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        CrashLogService.Write(e.Exception, "WPF Dispatcher");

        MessageBox.Show(
            "Ghost Server encountered an unexpected error and must close. A local diagnostic log was written to the GhostServer app-data Logs folder.",
            "Ghost Server",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = false;
    }

    private static void OnUnhandledException(
        object? sender,
        UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            CrashLogService.Write(exception, "AppDomain");
        }
    }

    private static void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        CrashLogService.Write(e.Exception, "TaskScheduler");
        e.SetObserved();
    }
}
