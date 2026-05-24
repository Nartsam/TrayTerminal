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

        _logger = new FileLogger(paths, "app");
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
