using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TrayTerminal.App.Services;
using TrayTerminal.Shared;

namespace TrayTerminal.App.Controls;

public sealed class TerminalView : System.Windows.Controls.UserControl, IDisposable
{
    private readonly PortablePaths _paths;
    private readonly TerminalSession _session;
    private readonly WebView2 _webView = new();
    private int _fontSize;
    private string? _backgroundImagePath;
    private bool _ready;
    private bool _disposed;

    public TerminalView(PortablePaths paths, TerminalSession session, int fontSize)
    {
        _paths = paths;
        _session = session;
        _fontSize = Math.Clamp(fontSize, 10, 32);
        Content = _webView;
    }

    public int Columns { get; private set; } = 120;

    public int Rows { get; private set; } = 30;

    public async Task InitializeAsync()
    {
        if (_ready)
        {
            return;
        }

        // WebView2 must use a fixed user-data folder to preserve the app's portable
        // data policy; otherwise Edge would create profile data in the user's profile.
        var environment = await CoreWebView2Environment.CreateAsync(null, _paths.WebView2DataDirectory);
        await _webView.EnsureCoreWebView2Async(environment);

        _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

        var htmlPath = _paths.TerminalHtmlPath;
        if (!File.Exists(htmlPath))
        {
            throw new FileNotFoundException("Terminal HTML asset was not copied to the output directory.", htmlPath);
        }

        var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // terminal.html posts a ready message after xterm/fallback input handlers exist.
        // Starting ConPTY before this point can lose the first burst of shell output.
        void ReadyHandler(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            if (document.RootElement.TryGetProperty("type", out var type) && type.GetString() == "ready")
            {
                readyTcs.TrySetResult();
                _webView.CoreWebView2.WebMessageReceived -= ReadyHandler;
            }
        }

        _webView.CoreWebView2.WebMessageReceived += ReadyHandler;
        _webView.Source = new Uri(htmlPath);
        await readyTcs.Task;
        _ready = true;
        await SetFontSizeAsync(_fontSize);
        await SetBackgroundImageAsync(_backgroundImagePath);
    }

    public async Task WriteAsync(byte[] bytes)
    {
        if (!_ready || _disposed)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(Convert.ToBase64String(bytes));
        await _webView.ExecuteScriptAsync($"window.trayTerminal.writeBase64({payload});");
    }

    public void SetFontSize(int fontSize)
    {
        _fontSize = Math.Clamp(fontSize, 10, 32);
        if (_ready && !_disposed)
        {
            _ = SetFontSizeAsync(_fontSize);
        }
    }

    public void SetBackgroundImage(string? imagePath)
    {
        _backgroundImagePath = imagePath;
        if (_ready && !_disposed)
        {
            _ = SetBackgroundImageAsync(_backgroundImagePath);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_webView.CoreWebView2 is not null)
        {
            _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
        }

        _webView.Dispose();
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var document = JsonDocument.Parse(e.WebMessageAsJson);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var typeProperty))
        {
            return;
        }

        switch (typeProperty.GetString())
        {
            case "input":
                if (root.TryGetProperty("data", out var data))
                {
                    await _session.SendInputAsync(data.GetString() ?? string.Empty);
                }
                break;

            case "resize":
                if (root.TryGetProperty("cols", out var cols) && root.TryGetProperty("rows", out var rows))
                {
                    Columns = Math.Clamp(cols.GetInt32(), 20, 500);
                    Rows = Math.Clamp(rows.GetInt32(), 5, 200);
                    await _session.ResizeAsync(Columns, Rows);
                }
                break;
        }
    }

    private async Task SetFontSizeAsync(int fontSize)
    {
        await _webView.ExecuteScriptAsync($"window.trayTerminal.setFontSize({fontSize});");
    }

    private async Task SetBackgroundImageAsync(string? imagePath)
    {
        var uri = string.IsNullOrWhiteSpace(imagePath) ? null : new Uri(imagePath).AbsoluteUri;
        var payload = JsonSerializer.Serialize(uri);
        await _webView.ExecuteScriptAsync($"window.trayTerminal.setBackground({payload});");
    }
}
