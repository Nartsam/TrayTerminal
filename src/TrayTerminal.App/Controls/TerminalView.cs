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
    private const int RpcEDisconnected = unchecked((int)0x80010108);
    private const int WebViewInitializationAttempts = 3;
    private static readonly TimeSpan InitializationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InitializationRetryDelay = TimeSpan.FromMilliseconds(300);
    // The terminal host emits chunks of at most 8 KB, so 1024 queued chunks cap the
    // backlog at roughly 8 MB if the WebView2 renderer ever stalls.
    private const int OutputQueueCapacity = 1024;
    private const int MaxScriptBatchBytes = 256 * 1024;
    private readonly PortablePaths _paths;
    private readonly TerminalSession _session;
    private readonly FileLogger _logger;
    private readonly WebView2EnvironmentManager _webView2EnvironmentManager;
    private readonly SemaphoreSlim _webViewGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
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
    private const int MaxRenderCrashRetries = 5;
    private static readonly TimeSpan RenderCrashResetWindow = TimeSpan.FromMinutes(5);
    private int _fontSize;
    private WebView2? _webView;
    private CoreWebView2? _coreWebView2;
    private CoreWebView2Environment? _environment;
    private TaskCompletionSource<WebView2> _webViewReady = CreateWebViewReadySource();
    private string? _backgroundImagePath;
    private string? _pendingRecoveryReason;
    private int _backgroundCover;
    private bool _ready;
    private bool _outputPumpStarted;
    private bool _disposed;
    private bool _scriptWriteFailing;
    private int _renderCrashCount;
    private int _recoveryFailed;
    private long _lastRenderCrashTicks;

    public TerminalView(
        PortablePaths paths,
        TerminalSession session,
        FileLogger logger,
        WebView2EnvironmentManager webView2EnvironmentManager,
        int fontSize)
    {
        _paths = paths;
        _session = session;
        _logger = logger;
        _webView2EnvironmentManager = webView2EnvironmentManager;
        _fontSize = Math.Clamp(fontSize, 10, 32);
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public event Action? OutputPumpStopped;

    public int Columns { get; private set; } = 120;

    public int Rows { get; private set; } = 30;

    public async Task InitializeAsync()
    {
        if (_ready)
        {
            return;
        }

        await _webViewGate.WaitAsync(_lifetimeCts.Token);
        try
        {
            if (_ready)
            {
                return;
            }

            await CreateAndInitializeWebViewAsync("initial terminal view initialization");
            if (!_outputPumpStarted)
            {
                _outputPumpStarted = true;
                // Started on the UI thread so every continuation resumes on the
                // dispatcher, which WebView2 requires.
                _ = PumpOutputAsync();
            }
        }
        finally
        {
            _webViewGate.Release();
        }
    }

    private async Task CreateAndInitializeWebViewAsync(string reason)
    {
        for (var attempt = 1; attempt <= WebViewInitializationAttempts; attempt++)
        {
            _lifetimeCts.Token.ThrowIfCancellationRequested();
            MarkWebViewNotReady();

            var webView = ReplaceCurrentWebView();
            CoreWebView2Environment? environment = null;
            try
            {
                // All tabs intentionally share this portable user-data folder and
                // therefore one browser process. The manager invalidates that
                // environment when the browser process exits.
                environment = await _webView2EnvironmentManager.GetEnvironmentAsync();
                _environment = environment;
                await InitializeWebViewCoreAsync(webView, environment);
                MarkWebViewReady(webView);
                Interlocked.Exchange(ref _recoveryFailed, 0);

                _logger.Info(
                    $"Terminal WebView2 initialized on browser process "
                    + $"{webView.CoreWebView2.BrowserProcessId} ({reason}).");
                return;
            }
            catch (Exception exception)
                when (IsBrowserDisconnected(exception) && attempt < WebViewInitializationAttempts)
            {
                if (environment is not null)
                {
                    _webView2EnvironmentManager.Invalidate(
                        environment,
                        $"initialization attempt {attempt} was disconnected");
                }

                _logger.Warn(
                    $"WebView2 disconnected during {reason}; retrying with a new "
                    + $"control and environment ({attempt}/{WebViewInitializationAttempts}).");
                await Task.Delay(InitializationRetryDelay, _lifetimeCts.Token);
            }
        }

        throw new InvalidOperationException("WebView2 initialization retry loop ended unexpectedly.");
    }

    private async Task InitializeWebViewCoreAsync(
        WebView2 webView,
        CoreWebView2Environment environment)
    {
        await webView.EnsureCoreWebView2Async(environment);
        if (!ReferenceEquals(webView, _webView))
        {
            throw new OperationCanceledException("The WebView2 control was replaced during initialization.");
        }

        var coreWebView2 = webView.CoreWebView2;
        _coreWebView2 = coreWebView2;
        coreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        coreWebView2.Settings.AreDevToolsEnabled = false;
        coreWebView2.WebMessageReceived += OnWebMessageReceived;
        coreWebView2.ProcessFailed += OnProcessFailed;

        var htmlPath = _paths.TerminalHtmlPath;
        if (!File.Exists(htmlPath))
        {
            throw new FileNotFoundException("Terminal HTML asset was not copied to the output directory.", htmlPath);
        }

        var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // terminal.html posts a ready message after xterm/fallback input handlers
        // exist. Starting ConPTY before this point can lose initial shell output.
        void ReadyHandler(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                using var document = JsonDocument.Parse(args.WebMessageAsJson);
                if (document.RootElement.TryGetProperty("type", out var type)
                    && type.GetString() == "ready")
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
            coreWebView2.WebMessageReceived += ReadyHandler;
            webView.Source = new Uri(htmlPath);
            await readyTcs.Task.WaitAsync(InitializationTimeout, _lifetimeCts.Token);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException("Timed out waiting for the terminal page to become ready.", exception);
        }
        finally
        {
            coreWebView2.WebMessageReceived -= ReadyHandler;
        }

        await SetFontSizeAsync(webView, _fontSize);
        await SetBackgroundImageWithErrorHandlingAsync(
            webView,
            _backgroundImagePath,
            _backgroundCover);
    }

    private WebView2 ReplaceCurrentWebView()
    {
        DisposeCurrentWebView();

        var webView = new WebView2();
        _webView = webView;
        Content = webView;
        return webView;
    }

    private void DisposeCurrentWebView()
    {
        var webView = _webView;
        var coreWebView2 = _coreWebView2;
        _webView = null;
        _coreWebView2 = null;
        _environment = null;

        if (coreWebView2 is not null)
        {
            try
            {
                coreWebView2.WebMessageReceived -= OnWebMessageReceived;
                coreWebView2.ProcessFailed -= OnProcessFailed;
            }
            catch (Exception exception) when (IsBrowserDisconnected(exception))
            {
                // The closed COM proxy cannot retain live callbacks after its
                // browser process has exited.
            }
        }

        if (webView is null)
        {
            return;
        }

        if (ReferenceEquals(Content, webView))
        {
            Content = null;
        }

        try
        {
            webView.Dispose();
        }
        catch (Exception exception) when (IsBrowserDisconnected(exception))
        {
            // A browser-process crash already closed the controller. Disposal is
            // still logically complete even if the COM proxy has disconnected.
        }
    }

    private void MarkWebViewNotReady()
    {
        _ready = false;
        if (_webViewReady.Task.IsCompleted)
        {
            _webViewReady = CreateWebViewReadySource();
        }
    }

    private void MarkWebViewReady(WebView2 webView)
    {
        _ready = true;
        _webViewReady.TrySetResult(webView);
    }

    private static TaskCompletionSource<WebView2> CreateWebViewReadySource()
    {
        return new TaskCompletionSource<WebView2>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static bool IsBrowserDisconnected(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.HResult == RpcEDisconnected)
            {
                return true;
            }
        }

        return false;
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
                var recoveryAttempts = 0;
                while (!_disposed)
                {
                    var webView = await WaitForReadyWebViewAsync();
                    if (webView is null)
                    {
                        return;
                    }

                    try
                    {
                        await webView.ExecuteScriptAsync($"window.trayTerminal.writeBase64({payload});");
                        _scriptWriteFailing = false;
                        break;
                    }
                    catch (Exception exception) when (IsBrowserDisconnected(exception))
                    {
                        if (++recoveryAttempts > WebViewInitializationAttempts
                            || !await RecoverWebViewAsync(
                                webView,
                                "terminal output delivery was disconnected",
                                exception))
                        {
                            return;
                        }
                    }
                    catch (Exception exception)
                    {
                        if (_disposed)
                        {
                            return;
                        }

                        // A renderer reload can reject an in-flight script without
                        // closing the browser process. Drop only this batch and keep
                        // the bounded output pump alive for the recovered page.
                        if (!_scriptWriteFailing)
                        {
                            _scriptWriteFailing = true;
                            _logger.Error(
                                exception,
                                "Terminal view failed to deliver output to WebView2; "
                                + "dropping output until the view recovers.");
                        }

                        break;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            if (!_disposed)
            {
                _logger.Error(exception, "Terminal view output pump stopped unexpectedly.");
                OutputPumpStopped?.Invoke();
            }
        }
    }

    private async Task<WebView2?> WaitForReadyWebViewAsync()
    {
        try
        {
            return await _webViewReady.Task.WaitAsync(_lifetimeCts.Token);
        }
        catch (OperationCanceledException) when (_disposed)
        {
            return null;
        }
    }

    private async Task<bool> RecoverWebViewAsync(
        WebView2 failedWebView,
        string reason,
        Exception? triggeringException = null)
    {
        try
        {
            await _webViewGate.WaitAsync(_lifetimeCts.Token);
        }
        catch (OperationCanceledException) when (_disposed)
        {
            return false;
        }

        try
        {
            if (_disposed)
            {
                return false;
            }

            // Another ProcessFailed callback or script operation may already have
            // recovered the shared view while this caller waited for the gate.
            if (!ReferenceEquals(_webView, failedWebView))
            {
                return _ready;
            }

            var failedEnvironment = _environment;
            MarkWebViewNotReady();
            if (failedEnvironment is not null)
            {
                _webView2EnvironmentManager.Invalidate(failedEnvironment, reason);
            }

            // A TabControl removes non-selected content from its visible tree.
            // Navigating a replacement WebView2 there can wait indefinitely for
            // the page's ready message, so keep the terminal session and bounded
            // output queue alive and recreate the view when the tab is selected.
            if (!IsVisible)
            {
                if (_pendingRecoveryReason is null)
                {
                    _logger.Warn(
                        $"Deferring terminal WebView2 recovery until its tab is visible: {reason}.");
                }

                _pendingRecoveryReason = reason;
                return true;
            }

            _pendingRecoveryReason = null;
            if (triggeringException is null)
            {
                _logger.Warn($"Recreating terminal WebView2 because {reason}.");
            }
            else
            {
                _logger.Warn(
                    $"Recreating terminal WebView2 because {reason}: "
                    + $"{triggeringException.Message}");
            }

            await CreateAndInitializeWebViewAsync($"recovery after {reason}");
            _logger.Info("Terminal WebView2 recovery completed.");
            return true;
        }
        catch (OperationCanceledException) when (_disposed)
        {
            return false;
        }
        catch (Exception exception)
        {
            _logger.Error(exception, $"Terminal WebView2 recovery failed after {reason}.");
            if (Interlocked.Exchange(ref _recoveryFailed, 1) == 0)
            {
                OutputPumpStopped?.Invoke();
            }

            return false;
        }
        finally
        {
            _webViewGate.Release();
        }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (_disposed
            || !IsVisible
            || _pendingRecoveryReason is not { } reason
            || _webView is not { } failedWebView)
        {
            return;
        }

        _pendingRecoveryReason = null;
        _ = RecoverWebViewAsync(
            failedWebView,
            $"deferred recovery after {reason}");
    }

    public void SetFontSize(int fontSize)
    {
        _fontSize = Math.Clamp(fontSize, 10, 32);
        var webView = _webView;
        if (_ready && !_disposed && webView is not null)
        {
            _ = UpdateFontSizeAsync(webView, _fontSize);
        }
    }

    public void SetBackgroundImage(string? imagePath, int cover = 0)
    {
        _backgroundImagePath = imagePath;
        _backgroundCover = Math.Clamp(cover, 0, 100);
        var webView = _webView;
        if (_ready && !_disposed && webView is not null)
        {
            _ = UpdateBackgroundImageAsync(
                webView,
                _backgroundImagePath,
                _backgroundCover);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsVisibleChanged -= OnIsVisibleChanged;
        _lifetimeCts.Cancel();
        _outputQueue.Writer.TryComplete();
        DisposeCurrentWebView();
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        var webView = _webView;
        if (_disposed || webView is null || !ReferenceEquals(sender, _coreWebView2))
        {
            return;
        }

        _logger.Error($"WebView2 process failed for a terminal view: kind={e.ProcessFailedKind}, reason={e.Reason}, exitCode={e.ExitCode}.");
        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            _ = RecoverWebViewAsync(
                webView,
                $"browser process exited ({e.Reason}, exit code {e.ExitCode})");
            return;
        }

        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited)
        {
            var now = Environment.TickCount64;
            if (now - _lastRenderCrashTicks > (long)RenderCrashResetWindow.TotalMilliseconds)
                _renderCrashCount = 0;
            _lastRenderCrashTicks = now;

            if (++_renderCrashCount <= MaxRenderCrashRetries)
            {
                try
                {
                    _coreWebView2?.Reload();
                }
                catch (Exception exception) when (IsBrowserDisconnected(exception))
                {
                    _ = RecoverWebViewAsync(
                        webView,
                        "renderer reload found a disconnected browser process",
                        exception);
                }
            }
            else
            {
                _logger.Error("WebView2 renderer crash limit reached; giving up reload.");
            }
        }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var webView = _webView;
            if (webView is null || !ReferenceEquals(sender, _coreWebView2))
            {
                return;
            }

            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeProperty))
            {
                return;
            }

            switch (typeProperty.GetString())
            {
                case "ready":
                    if (_ready)
                    {
                        try
                        {
                            await SetFontSizeAsync(webView, _fontSize);
                            await SetBackgroundImageWithErrorHandlingAsync(
                                webView,
                                _backgroundImagePath,
                                _backgroundCover);
                        }
                        catch (Exception exception) when (IsBrowserDisconnected(exception))
                        {
                            await RecoverWebViewAsync(
                                webView,
                                "appearance restore after reload was disconnected",
                                exception);
                        }
                        catch (Exception exception)
                        {
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
        catch (Exception exception)
        {
            _logger.Error(exception, "Failed to process WebView2 message.");
        }
    }

    private async Task UpdateFontSizeAsync(WebView2 webView, int fontSize)
    {
        try
        {
            await SetFontSizeAsync(webView, fontSize);
        }
        catch (Exception exception) when (IsBrowserDisconnected(exception))
        {
            await RecoverWebViewAsync(
                webView,
                "font-size update was disconnected",
                exception);
        }
        catch (Exception exception)
        {
            if (!_disposed)
            {
                _logger.Error(exception, "Failed to update terminal font size.");
            }
        }
    }

    private async Task UpdateBackgroundImageAsync(
        WebView2 webView,
        string? imagePath,
        int cover)
    {
        try
        {
            await SetBackgroundImageWithErrorHandlingAsync(webView, imagePath, cover);
        }
        catch (Exception exception) when (IsBrowserDisconnected(exception))
        {
            await RecoverWebViewAsync(
                webView,
                "background update was disconnected",
                exception);
        }
        catch (Exception exception)
        {
            if (!_disposed)
            {
                _logger.Error(exception, "Failed to update terminal background.");
            }
        }
    }

    private static async Task SetFontSizeAsync(WebView2 webView, int fontSize)
    {
        await webView.ExecuteScriptAsync($"window.trayTerminal.setFontSize({fontSize});");
    }

    private static async Task SetBackgroundImageAsync(
        WebView2 webView,
        string? imagePath,
        int cover)
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
        await webView.ExecuteScriptAsync(
            $"window.trayTerminal.setBackground({payload}, {Math.Clamp(cover, 0, 100)});");
    }

    private static async Task SetBackgroundImageWithErrorHandlingAsync(
        WebView2 webView,
        string? imagePath,
        int cover)
    {
        try
        {
            await SetBackgroundImageAsync(webView, imagePath, cover);
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

        var webView = _webView;
        if (webView is null)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(text);
        try
        {
            await webView.ExecuteScriptAsync($"window.trayTerminal.pasteText({payload});");
        }
        catch (Exception exception) when (IsBrowserDisconnected(exception))
        {
            if (await RecoverWebViewAsync(
                    webView,
                    "paste operation was disconnected",
                    exception)
                && _webView is { } recoveredWebView)
            {
                await recoveredWebView.ExecuteScriptAsync(
                    $"window.trayTerminal.pasteText({payload});");
            }
        }
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
