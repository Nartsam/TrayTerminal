using System.IO;
using System.Windows;
using TrayTerminal.App.Dialogs;
using TrayTerminal.Shared;

namespace TrayTerminal.App;

public partial class App : System.Windows.Application
{
    private FileLogger? _logger;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = new PortablePaths();
        paths.EnsureCreated();
        Directory.SetCurrentDirectory(paths.BaseDirectory);

        FileLogger.LoadSettings(paths);
        _logger = new FileLogger(paths, "app");

        // Last-resort diagnostics for long unattended runs: without these, a crash
        // outside the dispatcher (background thread) or a faulted forgotten task
        // would leave no trace in the logs.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                _logger.Error(exception, "Unhandled exception (AppDomain).");
            }
            else
            {
                _logger.Error($"Unhandled exception (AppDomain): {args.ExceptionObject}");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger.Error(args.Exception, "Unobserved task exception.");
            args.SetObserved();
        };

        DispatcherUnhandledException += (_, args) =>
        {
            _logger.Error(args.Exception, "Unhandled UI exception.");
            AppMessageDialog.Info(MainWindow as Window ?? new Window(), args.Exception.Message);
            args.Handled = true;
        };

        MainWindow = new MainWindow(paths, _logger);
        MainWindow.Show();
    }
}
