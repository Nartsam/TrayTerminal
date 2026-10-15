using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using TrayTerminal.App.Dialogs;
using TrayTerminal.App.Services;
using TrayTerminal.Shared;
using MediaBrush = System.Windows.Media.Brush;

namespace TrayTerminal.App.Controls;

public sealed class TerminalPage : System.Windows.Controls.UserControl, IAsyncDisposable
{
    private readonly TerminalSession _session;
    private readonly TerminalView _terminalView;
    private readonly TextBlock _status;
    private readonly Ellipse _statusDot;
    private bool _disposed;

    public TerminalPage(
        NewTerminalRequest request,
        PortablePaths paths,
        FileLogger logger,
        WebView2EnvironmentManager webView2EnvironmentManager,
        TerminalAuthorityHost terminalAuthorityHost,
        int fontSize)
    {
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
            Foreground = FindBrush("MutedTextBrush"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _statusDot = new Ellipse
        {
            Width = 7,
            Height = 7,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        SetStatus(
            request.RunAsAdministrator ? "准备启动管理员终端..." : "准备启动终端...",
            "StatusIdleBrush");

        var statusContent = new DockPanel();
        DockPanel.SetDock(_statusDot, Dock.Left);
        statusContent.Children.Add(_statusDot);
        statusContent.Children.Add(_status);
        var statusBar = new Border
        {
            Background = FindBrush("WindowBackgroundBrush"),
            Height = 26,
            Padding = new Thickness(12, 0, 12, 0),
            Child = statusContent
        };

        // No frame: the terminal surface continues the selected tab above it.
        var surface = new Border
        {
            Background = FindBrush("TerminalBackgroundBrush"),
            Child = _terminalView
        };

        var root = new DockPanel();
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(statusBar);
        root.Children.Add(surface);
        Content = root;

        // EnqueueOutput is thread-safe and non-blocking, so the session read loop
        // never waits on the UI thread or the WebView2 renderer.
        _session.OutputReceived += (_, output) => _terminalView.EnqueueOutput(output);
        _session.Exited += (_, exitCode) =>
        {
            Dispatcher.BeginInvoke(() => SetStatus($"进程已退出，代码 {exitCode}", "StatusIdleBrush"));
            Exited?.Invoke(this, exitCode);
        };
        _session.Failed += (_, message) => Dispatcher.BeginInvoke(() =>
        {
            SetStatus(message, "StatusErrorBrush");
        });
        _session.AuthorityDegraded += (_, reason) => Dispatcher.BeginInvoke(() =>
        {
            SetStatus(
                $"{(_session.IsAdministrator ? "管理员终端" : "终端")}运行中；"
                + $"状态同步已停用，远程访问不可用（{reason}）",
                "StatusWarningBrush");
        });
        _terminalView.OutputPumpStopped += () => Dispatcher.BeginInvoke(() =>
        {
            SetStatus("终端显示已中断，终端进程可能仍在运行", "StatusWarningBrush");
        });
    }

    public event EventHandler<int>? Exited;

    public string Title { get; private set; }

    public int TerminalFontSize { get; private set; }

    public string? BackgroundImagePath { get; private set; }

    public int BackgroundCover { get; private set; }

    public bool IsRunning => _session.IsRunning;

    public TerminalSession Session => _session;

    public async Task StartAsync()
    {
        await _terminalView.InitializeAsync();
        var size = _session.CurrentSize;
        await _session.StartAsync(size.Columns, size.Rows);
        if (_session.IsRunning && !_session.IsAuthorityDegraded)
        {
            SetStatus(
                _session.ElevationDeclined
                    ? "已取消管理员授权，已改为普通权限终端运行"
                    : _session.IsAdministrator ? "管理员终端运行中" : "终端运行中",
                "StatusRunningBrush");
        }
    }

    private static MediaBrush FindBrush(string key)
    {
        return (MediaBrush)System.Windows.Application.Current.FindResource(key);
    }

    /// <summary>Shows a status line with a dot colored by one of the Status*Brush resources.</summary>
    private void SetStatus(string text, string dotBrushKey)
    {
        _status.Text = text;
        _statusDot.Fill = FindBrush(dotBrushKey);
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
