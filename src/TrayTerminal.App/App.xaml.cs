using System.IO;
using System.Windows;
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
            System.Windows.MessageBox.Show(args.Exception.Message, "TrayTerminal", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        MainWindow = new MainWindow(paths, _logger);
        MainWindow.Show();
    }
}
