using System.IO;
using System.Windows;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Controls;
using TrayTerminal.App.Dialogs;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TrayTerminal.App.Services;
using TrayTerminal.Shared;

namespace TrayTerminal.App.Controls;

public sealed class TerminalView : System.Windows.Controls.UserControl, IDisposable
{
    private const int MaxPastePreviewCharacters = 1600;
    private const int MaxPastePreviewLines = 20;
    private static readonly TimeSpan InitializationTimeout = TimeSpan.FromSeconds(30);
    // The terminal host emits chunks of at most 8 KB, so 1024 queued chunks cap the
    // backlog at roughly 8 MB if the WebView2 renderer ever stalls.
    private const int OutputQueueCapacity = 1024;
    private const int MaxScriptBatchBytes = 256 * 1024;
    private readonly PortablePaths _paths;
    private readonly TerminalSession _session;
    private readonly FileLogger _logger;
    private readonly WebView2 _webView = new();
    private readonly Channel<byte[]> _outputQueue = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(OutputQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            // If the renderer stops draining (crashed/unresponsive), keep the newest
            // output and drop the oldest instead of stalling the session read loop —
            // remote-access bridges share that loop and must keep flowing.
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private int _fontSize;
    private string? _backgroundImagePath;
    private int _backgroundCover;
    private bool _ready;
    private bool _disposed;
    private bool _scriptWriteFailing;

    public TerminalView(PortablePaths paths, TerminalSession session, FileLogger logger, int fontSize)
    {
        _paths = paths;
        _session = session;
        _logger = logger;
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
        _webView.CoreWebView2.ProcessFailed += OnProcessFailed;

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
            try
            {
                using var document = JsonDocument.Parse(args.WebMessageAsJson);
                if (document.RootElement.TryGetProperty("type", out var type) && type.GetString() == "ready")
                {
                    readyTcs.TrySetResult();
                }
            }
            catch (JsonException)
            {
            }
        }

        try
        {
            _webView.CoreWebView2.WebMessageReceived += ReadyHandler;
            _webView.Source = new Uri(htmlPath);
            await readyTcs.Task.WaitAsync(InitializationTimeout);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException("Timed out waiting for the terminal page to become ready.", ex);
        }
        finally
        {
            if (_webView.CoreWebView2 is not null)
            {
                _webView.CoreWebView2.WebMessageReceived -= ReadyHandler;
            }
        }

        _ready = true;
        // Started on the UI thread so every continuation resumes on the dispatcher,
        // which WebView2 requires. The pump never throws (all failures are handled
        // inside), so fire-and-forget is safe here.
        _ = PumpOutputAsync();
        await SetFontSizeAsync(_fontSize);
        try
        {
            await SetBackgroundImageAsync(_backgroundImagePath, _backgroundCover);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"TerminalView: failed to load background image '{_backgroundImagePath}': {ex.Message}");
        }
    }

    /// <summary>
    /// Queues terminal output for delivery to xterm.js. Thread-safe and non-blocking;
    /// called directly from the session read loop without a dispatcher hop.
    /// </summary>
    public void EnqueueOutput(byte[] bytes)
    {
        if (!_disposed)
        {
            _outputQueue.Writer.TryWrite(bytes);
        }
    }

    private async Task PumpOutputAsync()
    {
        // Single in-flight ExecuteScriptAsync at a time: adjacent chunks are merged
        // into one script call, and a slow renderer backs up into the bounded queue
        // instead of piling up pending WebView2 IPC messages without limit.
        var batch = new MemoryStream();
        try
        {
            while (await _outputQueue.Reader.WaitToReadAsync())
            {
                batch.SetLength(0);
                while (batch.Length < MaxScriptBatchBytes && _outputQueue.Reader.TryRead(out var chunk))
                {
                    batch.Write(chunk, 0, chunk.Length);
                }

                if (_disposed)
                {
                    return;
                }

                if (batch.Length == 0)
                {
                    continue;
                }

                var payload = JsonSerializer.Serialize(Convert.ToBase64String(batch.GetBuffer(), 0, (int)batch.Length));
                try
                {
                    await _webView.ExecuteScriptAsync($"window.trayTerminal.writeBase64({payload});");
                    _scriptWriteFailing = false;
                }
                catch (Exception exception)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    // The renderer may be gone (crash, reload in progress). Drop this
                    // batch and keep pumping so output resumes once the view recovers;
                    // log only the first failure of a streak to avoid log spam.
                    if (!_scriptWriteFailing)
                    {
                        _scriptWriteFailing = true;
                        _logger.Error(exception, "Terminal view failed to deliver output to WebView2; dropping output until the view recovers.");
                    }
                }
            }
        }
        catch (Exception exception)
        {
            if (!_disposed)
            {
                _logger.Error(exception, "Terminal view output pump stopped unexpectedly.");
            }
        }
    }

    public void SetFontSize(int fontSize)
    {
        _fontSize = Math.Clamp(fontSize, 10, 32);
        if (_ready && !_disposed)
        {
            _ = SetFontSizeAsync(_fontSize);
        }
    }

    public void SetBackgroundImage(string? imagePath, int cover = 0)
    {
        _backgroundImagePath = imagePath;
        _backgroundCover = Math.Clamp(cover, 0, 100);
        if (_ready && !_disposed)
        {
            _ = SetBackgroundImageWithErrorHandlingAsync(_backgroundImagePath, _backgroundCover);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _outputQueue.Writer.TryComplete();
        if (_webView.CoreWebView2 is not null)
        {
            _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            _webView.CoreWebView2.ProcessFailed -= OnProcessFailed;
        }

        _webView.Dispose();
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _logger.Error($"WebView2 process failed for a terminal view: kind={e.ProcessFailedKind}, reason={e.Reason}, exitCode={e.ExitCode}.");
        if (!_disposed && e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited)
        {
            // Reload restarts the crashed renderer; terminal.html posts "ready" again
            // and OnWebMessageReceived reapplies the font size and background.
            _webView.Reload();
        }
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
            case "ready":
                // The first load is handled by InitializeAsync (where _ready is still
                // false). A later "ready" means the renderer was reloaded after a
                // crash — reapply per-tab appearance.
                if (_ready)
                {
                    try
                    {
                        await SetFontSizeAsync(_fontSize);
                        await SetBackgroundImageWithErrorHandlingAsync(_backgroundImagePath, _backgroundCover);
                    }
                    catch (Exception exception)
                    {
                        // The renderer may have died again mid-reapply; never let this
                        // bubble into the dispatcher on an unattended machine.
                        _logger.Error(exception, "Failed to reapply terminal appearance after a WebView2 reload.");
                    }
                }
                break;

            case "input":
                if (root.TryGetProperty("data", out var data))
                {
                    await _session.SendInputAsync(data.GetString() ?? string.Empty);
                }
                break;

            case "copy":
                if (root.TryGetProperty("data", out var copyData))
                {
                    SetClipboardText(copyData.GetString() ?? string.Empty);
                }
                break;

            case "paste":
                var clipboardText = GetClipboardText();
                if (!string.IsNullOrEmpty(clipboardText))
                {
                    if (IsMultilineText(clipboardText) && !ConfirmMultilinePaste(clipboardText))
                    {
                        break;
                    }

                    await PasteTextAsync(clipboardText);
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

    private async Task SetBackgroundImageAsync(string? imagePath, int cover)
    {
        // Read the image file and convert to a data: URL so the WebView2 engine does
        // not cache the image across tab creations or config changes.
        string? dataUri;
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            dataUri = null;
        }
        else
        {
            var bytes = await File.ReadAllBytesAsync(imagePath);
            var base64 = Convert.ToBase64String(bytes);
            var mime = Path.GetExtension(imagePath).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                _ => "image/png"
            };
            dataUri = $"data:{mime};base64,{base64}";
        }

        var payload = JsonSerializer.Serialize(dataUri);
        await _webView.ExecuteScriptAsync(
            $"window.trayTerminal.setBackground({payload}, {Math.Clamp(cover, 0, 100)});");
    }

    private async Task SetBackgroundImageWithErrorHandlingAsync(string? imagePath, int cover)
    {
        try
        {
            await SetBackgroundImageAsync(imagePath, cover);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fire-and-forget path — log the error but don't crash.
            System.Diagnostics.Debug.WriteLine(
                $"TerminalView: failed to set background image '{imagePath}': {ex.Message}");
        }
    }

    private async Task PasteTextAsync(string text)
    {
        if (!_ready || _disposed)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(text);
        await _webView.ExecuteScriptAsync($"window.trayTerminal.pasteText({payload});");
    }

    private bool ConfirmMultilinePaste(string text)
    {
        var owner = Window.GetWindow(this) ?? System.Windows.Application.Current.MainWindow;
        if (owner is null)
        {
            return false;
        }

        var preview = BuildPastePreview(text, out var truncated);
        var lineCount = CountLines(text);
        var message = $"剪贴板内容包含 {lineCount} 行、{text.Length} 个字符，确定要粘贴到当前终端吗？\n预览如下{(truncated ? "（已截断）" : string.Empty)}：";
        return AppMessageDialog.ConfirmWithPreview(owner, message, preview, "粘贴", "取消");
    }

    private static bool IsMultilineText(string text)
    {
        return text.Contains('\n') || text.Contains('\r');
    }

    private static string BuildPastePreview(string text, out bool truncated)
    {
        var normalized = NormalizeNewlines(text);
        var lines = normalized.Split('\n');
        var preview = string.Join(Environment.NewLine, lines.Take(MaxPastePreviewLines));
        truncated = lines.Length > MaxPastePreviewLines;

        if (preview.Length > MaxPastePreviewCharacters)
        {
            preview = preview[..MaxPastePreviewCharacters];
            truncated = true;
        }

        return truncated ? preview + Environment.NewLine + "..." : preview;
    }

    private static int CountLines(string text)
    {
        return NormalizeNewlines(text).Split('\n').Length;
    }

    private static string NormalizeNewlines(string text)
    {
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    private static string GetClipboardText()
    {
        try
        {
            return System.Windows.Clipboard.ContainsText(System.Windows.TextDataFormat.UnicodeText)
                ? System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.UnicodeText)
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void SetClipboardText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(text, System.Windows.TextDataFormat.UnicodeText);
        }
        catch
        {
        }
    }
}
