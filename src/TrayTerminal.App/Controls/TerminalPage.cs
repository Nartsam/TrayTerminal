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
    private NewTerminalRequest _request;
    private readonly TerminalSession _session;
    private readonly TerminalView _terminalView;
    private readonly TextBlock _status;
    private readonly System.Windows.Controls.Button _recreateButton;
    private bool _disposed;

    public TerminalPage(
        NewTerminalRequest request,
        PortablePaths paths,
        FileLogger logger,
        WebView2EnvironmentManager webView2EnvironmentManager,
        TerminalAuthorityHost terminalAuthorityHost,
        int fontSize)
    {
        _request = request;
        Title = request.Title;
        TerminalFontSize = fontSize;
        _session = new TerminalSession(
            request,
            paths,
            logger,
            terminalAuthorityHost);
        _terminalView = new TerminalView(
            paths,
            _session,
            logger,
            webView2EnvironmentManager,
            fontSize);
        _status = new TextBlock
        {
            Text = request.RunAsAdministrator ? "准备启动管理员终端..." : "准备启动终端...",
            Foreground = (MediaBrush)System.Windows.Application.Current.FindResource("MutedTextBrush"),
            FontSize = 12,
            Padding = new Thickness(10, 4, 10, 4)
        };
        _recreateButton = new System.Windows.Controls.Button
        {
            Content = "重建终端",
            Margin = new Thickness(8, 2, 8, 2),
            Padding = new Thickness(10, 2, 10, 2),
            Visibility = Visibility.Collapsed
        };
        _recreateButton.Click += (_, _) => RecreateRequested?.Invoke(this, EventArgs.Empty);

        var border = new Border
        {
            BorderBrush = (MediaBrush)System.Windows.Application.Current.FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            Background = MediaBrushes.Black,
            Child = _terminalView
        };

        var statusBar = new DockPanel();
        DockPanel.SetDock(_recreateButton, Dock.Right);
        statusBar.Children.Add(_recreateButton);
        statusBar.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(statusBar);
        root.Children.Add(border);
        Content = root;

        // EnqueueOutput is thread-safe and non-blocking, so the session read loop
        // never waits on the UI thread or the WebView2 renderer.
        _session.OutputReceived += (_, output) => _terminalView.EnqueueOutput(output);
        _session.Exited += (_, exitCode) =>
        {
            Dispatcher.BeginInvoke(() => _status.Text = $"进程已退出，代码 {exitCode}");
            Exited?.Invoke(this, exitCode);
        };
        _session.Failed += (_, message) => Dispatcher.BeginInvoke(() =>
        {
            _status.Text = message;
            _recreateButton.Visibility = Visibility.Visible;
        });
        _terminalView.OutputPumpStopped += () => Dispatcher.BeginInvoke(() =>
        {
            _status.Text = "终端输出已中断，建议重建标签页";
        });
    }

    public event EventHandler<int>? Exited;

    public event EventHandler? RecreateRequested;

    public string Title { get; private set; }

    public int TerminalFontSize { get; private set; }

    public string? BackgroundImagePath { get; private set; }

    public int BackgroundCover { get; private set; }

    public bool IsRunning => _session.IsRunning;

    public TerminalSession Session => _session;

    public NewTerminalRequest Request => _request;

    public async Task StartAsync()
    {
        await _terminalView.InitializeAsync();
        var size = _session.CurrentSize;
        await _session.StartAsync(size.Columns, size.Rows);
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
        _request = _request with { Title = title };
    }

    public async Task<BackgroundImageUpdateResult> SetBackgroundImageAsync(
        string? imagePath,
        int cover = 0)
    {
        var normalizedCover = Math.Clamp(cover, 0, 100);
        var result = await _terminalView.SetBackgroundImageAsync(
            imagePath,
            normalizedCover);
        if (result.Applied)
        {
            BackgroundImagePath = imagePath;
            BackgroundCover = normalizedCover;
        }

        return result;
    }

    public async Task SendInputAsync(string text)
    {
        await _session.SendInputAsync(text);
    }

    public Task<TerminalCloseCheckLease> BeginCloseCheckAsync(
        CancellationToken cancellationToken = default)
    {
        return _session.BeginCloseCheckAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _terminalView.Dispose();
        await _session.DisposeAsync();
    }

    public void KillForShutdown()
    {
        _session.KillForShutdown();
    }
}
