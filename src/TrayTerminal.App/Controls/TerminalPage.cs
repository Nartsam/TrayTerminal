using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TrayTerminal.App.Dialogs;
using TrayTerminal.App.Services;
using TrayTerminal.Shared;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;

namespace TrayTerminal.App.Controls;

public sealed class TerminalPage : System.Windows.Controls.UserControl, IAsyncDisposable
{
    private readonly TerminalSession _session;
    private readonly TerminalView _terminalView;
    private readonly TextBlock _status;
    private bool _disposed;

    public TerminalPage(NewTerminalRequest request, PortablePaths paths, FileLogger logger, int fontSize)
    {
        Title = request.Title;
        TerminalFontSize = fontSize;
        _session = new TerminalSession(request, paths, logger);
        _terminalView = new TerminalView(paths, _session, fontSize);
        _status = new TextBlock
        {
            Text = request.RunAsAdministrator ? "准备启动管理员终端..." : "准备启动终端...",
            Foreground = (MediaBrush)System.Windows.Application.Current.FindResource("MutedTextBrush"),
            FontSize = 12,
            Padding = new Thickness(10, 4, 10, 4)
        };

        var border = new Border
        {
            BorderBrush = (MediaBrush)System.Windows.Application.Current.FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            Background = MediaBrushes.Black,
            Child = _terminalView
        };

        var root = new DockPanel();
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);
        root.Children.Add(border);
        Content = root;

        _session.OutputReceived += (_, bytes) => Dispatcher.Invoke(() => _ = _terminalView.WriteAsync(bytes));
        _session.Exited += (_, exitCode) =>
        {
            Dispatcher.Invoke(() => _status.Text = $"进程已退出，代码 {exitCode}");
            Exited?.Invoke(this, exitCode);
        };
        _session.Failed += (_, message) => Dispatcher.Invoke(() => _status.Text = message);
    }

    public event EventHandler<int>? Exited;

    public string Title { get; private set; }

    public int TerminalFontSize { get; private set; }

    public string? BackgroundImagePath { get; private set; }

    public int BackgroundCover { get; private set; }

    public bool IsRunning => _session.IsRunning;

    public async Task StartAsync()
    {
        await _terminalView.InitializeAsync();
        await _session.StartAsync(_terminalView.Columns, _terminalView.Rows);
        if (_session.IsRunning)
        {
            _status.Text = _session.IsAdministrator ? "管理员终端运行中" : "终端运行中";
        }
    }

    public void SetFontSize(int fontSize)
    {
        TerminalFontSize = Math.Clamp(fontSize, 10, 32);
        _terminalView.SetFontSize(TerminalFontSize);
    }

    public void SetTitle(string title)
    {
        Title = title;
    }

    public void SetBackgroundImage(string? imagePath, int cover = 0)
    {
        BackgroundImagePath = imagePath;
        BackgroundCover = Math.Clamp(cover, 0, 100);
        _terminalView.SetBackgroundImage(imagePath, BackgroundCover);
    }

    public async Task SendInputAsync(string text)
    {
        await _session.SendInputAsync(text);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _session.DisposeAsync();
        _terminalView.Dispose();
    }

    public void KillForShutdown()
    {
        _session.KillForShutdown();
    }
}
