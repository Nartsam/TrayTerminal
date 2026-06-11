using System.IO;
using System.Windows;
using System.Windows.Threading;
using TrayTerminal.App.Dialogs;
using TrayTerminal.Shared;

namespace TrayTerminal.App;

public partial class App : System.Windows.Application
{
    private FileLogger? _logger;
    private Window? _errorDialog;

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
            args.Handled = true;

            var owner = MainWindow as Window;
            if (owner is null) return;

            _errorDialog?.Close();
            _errorDialog = null;

            var dialog = new AppMessageDialog(args.Exception.Message, ["确定"], owner);
            _errorDialog = dialog;
            dialog.Closed += (_, _) => { if (_errorDialog == dialog) _errorDialog = null; };
            dialog.Show();

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try { dialog.Close(); } catch { }
            };
            timer.Start();
        };

        MainWindow = new MainWindow(paths, _logger);
        MainWindow.Show();
    }
}
