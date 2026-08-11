using System.Collections.Concurrent;
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
using TrayTerminal.Shared.Terminal;

namespace TrayTerminal.App.Controls;

public readonly record struct BackgroundImageUpdateResult(
    bool Applied,
    string? ErrorMessage)
{
    public static BackgroundImageUpdateResult Success() => new(true, null);

    public static BackgroundImageUpdateResult Failure(string message) => new(false, message);
}

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
    private const int MaxBackgroundImageBytes = 32 * 1024 * 1024;
    private const int BackgroundReadBufferSize = 64 * 1024;
    private readonly PortablePaths _paths;
    private readonly TerminalSession _session;
    private readonly FileLogger _logger;
    private readonly WebView2EnvironmentManager _webView2EnvironmentManager;
    private readonly Guid _terminalClientId;
    private readonly SemaphoreSlim _webViewGate = new(1, 1);
    private readonly SemaphoreSlim _backgroundUpdateGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingBatches = new();
    private readonly Channel<TerminalOutput> _outputQueue = Channel.CreateBounded<TerminalOutput>(
        new BoundedChannelOptions(OutputQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            // Producers use TryWrite. A full queue creates a detectable sequence
            // gap and forces state recovery; silently dropping the oldest chunk
            // would let a partial terminal state masquerade as authoritative.
            FullMode = BoundedChannelFullMode.Wait
        });
    private const int MaxRenderCrashRetries = 5;
    private static readonly TimeSpan RenderCrashResetWindow = TimeSpan.FromMinutes(5);
    private int _fontSize;
    private WebView2? _webView;
    private CoreWebView2? _coreWebView2;
    private CoreWebView2Environment? _environment;
    private TaskCompletionSource<WebView2> _webViewReady = CreateWebViewReadySource();
    private string? _backgroundDataUri;
    private string? _pendingRecoveryReason;
    private bool _pendingRecoveryInvalidatesEnvironment;
    private int _backgroundCover;
    private bool _ready;
    private bool _outputPumpStarted;
    private bool _disposed;
    private bool _scriptWriteFailing;
    private int _renderCrashCount;
    private int _recoveryFailed;
    private int _outputQueueOverflow;
    private int _logicalRecoveryScheduled;
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
        _terminalClientId = _session.RegisterTerminalClient(
            TerminalClientKind.Local,
            IsVisible,
            Columns,
            Rows);
        _session.SizeChanged += OnSessionSizeChanged;
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
        await SetBackgroundDataUriAsync(
            webView,
            _backgroundDataUri,
            _backgroundCover,
            _lifetimeCts.Token);
        await RestoreAuthoritativeStateAsync(webView);
        if (IsVisible && _session.IsSizeOwner(_terminalClientId))
        {
            await RequestLocalSizeEstimateCoreAsync(webView);
        }
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
    public void EnqueueOutput(TerminalOutput output)
    {
        if (!_disposed
            && !_outputQueue.Writer.TryWrite(output)
            && Interlocked.Exchange(ref _outputQueueOverflow, 1) == 0)
        {
            _logger.Warn(
                "Terminal renderer queue overflowed; rebuilding it from the "
                + "bounded checkpoint.");
            _ = ScheduleLogicalRecoveryAsync(
                "terminal renderer output queue overflow");
        }
    }

    private async Task PumpOutputAsync()
    {
        // Single in-flight ExecuteScriptAsync at a time. Each batch keeps chunk
        // boundaries so recovery can discard already checkpointed sequences
        // without duplicating a prefix of a merged VT byte stream.
        var batch = new List<TerminalOutput>();
        try
        {
            while (await _outputQueue.Reader.WaitToReadAsync())
            {
                batch.Clear();
                var batchBytes = 0;
                while (batchBytes < MaxScriptBatchBytes
                    && _outputQueue.Reader.TryRead(out var output))
                {
                    batch.Add(output);
                    batchBytes += output.Data.Length;
                }

                if (_disposed)
                {
                    return;
                }

                if (batch.Count == 0)
                {
                    continue;
                }

                var payload = JsonSerializer.Serialize(
                    batch.Select(output => new
                    {
                        type = output.IsResize ? "resize" : "output",
                        sequence = output.Sequence.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        data = output.IsResize
                            ? null
                            : Convert.ToBase64String(output.Data),
                        cols = output.Resize?.Columns,
                        rows = output.Resize?.Rows
                    }));
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
                        var batchId = Guid.NewGuid().ToString("N");
                        var applied = new TaskCompletionSource<bool>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        if (!_pendingBatches.TryAdd(batchId, applied))
                        {
                            throw new InvalidOperationException(
                                "Duplicate local terminal batch ID.");
                        }

                        try
                        {
                            var accepted = await webView.ExecuteScriptAsync(
                                $"window.trayTerminal.writeSequencedBatch("
                                + $"{payload},{JsonSerializer.Serialize(batchId)});");
                            if (!string.Equals(
                                    accepted,
                                    "true",
                                    StringComparison.OrdinalIgnoreCase)
                                || !await applied.Task.WaitAsync(
                                    InitializationTimeout,
                                    _lifetimeCts.Token))
                            {
                                throw new InvalidOperationException(
                                    "The local terminal replica rejected an output batch.");
                            }
                        }
                        finally
                        {
                            _pendingBatches.TryRemove(batchId, out _);
                        }
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
        Exception? triggeringException = null,
        bool invalidateEnvironment = true)
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
            if (invalidateEnvironment && failedEnvironment is not null)
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
                _pendingRecoveryInvalidatesEnvironment |= invalidateEnvironment;
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
        if (_disposed)
        {
            return;
        }

        _ = UpdateLocalVisibilityAsync();

        if (!IsVisible
            || _pendingRecoveryReason is not { } reason
            || _webView is not { } failedWebView)
        {
            return;
        }

        _pendingRecoveryReason = null;
        var invalidateEnvironment = _pendingRecoveryInvalidatesEnvironment;
        _pendingRecoveryInvalidatesEnvironment = false;
        _ = RecoverWebViewAsync(
            failedWebView,
            $"deferred recovery after {reason}",
            invalidateEnvironment: invalidateEnvironment);
    }

    private async Task UpdateLocalVisibilityAsync()
    {
        try
        {
            await _session.SetLocalClientVisibilityAsync(
                _terminalClientId,
                IsVisible,
                Columns,
                Rows);
            if (IsVisible
                && _session.IsSizeOwner(_terminalClientId)
                && _webView is { } webView
                && _ready)
            {
                // Columns/Rows currently reflect the remote canonical size. Once
                // local visibility wins the lease, remeasure the real DOM cells
                // instead of waiting for a window.resize event that may never fire.
                await RequestLocalSizeEstimateFromWebViewAsync(webView);
            }
        }
        catch (Exception exception)
        {
            if (!_disposed)
            {
                _logger.Error(exception, "Failed to update local terminal size ownership.");
            }
        }
    }

    private void OnSessionSizeChanged(object? sender, TerminalSizeUpdate update)
    {
        Columns = update.Size.Columns;
        Rows = update.Size.Rows;
        // Rendering resize is delivered only through the sequenced output
        // stream. Applying this ownership notification directly could race
        // ahead of older output that was already queued for the replica.
    }

    private async Task RestoreAuthoritativeStateAsync(WebView2 webView)
    {
        if (!_session.TryGetRecoveryState(out var state) || state is null)
        {
            throw new InvalidOperationException(
                "Terminal state can no longer be reconstructed because its bounded "
                + "checkpoint tail overflowed before the renderer recovered. "
                + "Rebuild the terminal tab.");
        }

        var expectedSequence = state.Snapshot.Sequence + 1;
        foreach (var output in state.Replay)
        {
            if (output.Sequence != expectedSequence)
            {
                throw new InvalidDataException(
                    "Terminal checkpoint replay contains an output sequence gap.");
            }

            expectedSequence++;
        }

        if (expectedSequence - 1 != state.LatestSequence)
        {
            throw new InvalidDataException(
                "Terminal checkpoint replay does not reach the latest output.");
        }

        var snapshot = JsonSerializer.Serialize(
            Convert.ToBase64String(state.Snapshot.Data));
        var replayPayload = JsonSerializer.Serialize(
            state.Replay.Select(output => new
            {
                type = output.IsResize ? "resize" : "output",
                sequence = output.Sequence.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                data = output.IsResize
                    ? null
                    : Convert.ToBase64String(output.Data),
                cols = output.Resize?.Columns,
                rows = output.Resize?.Rows
            }));
        var restoreId = Guid.NewGuid().ToString("N");
        var restored = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void RestoreHandler(
            object? sender,
            CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                using var document = JsonDocument.Parse(args.WebMessageAsJson);
                var root = document.RootElement;
                if (root.TryGetProperty("type", out var type)
                    && type.GetString() == "stateRestored"
                    && root.TryGetProperty("restoreId", out var id)
                    && id.GetString() == restoreId)
                {
                    restored.TrySetResult(true);
                }
            }
            catch (JsonException)
            {
            }
        }

        var restoreCoreWebView2 = webView.CoreWebView2;
        restoreCoreWebView2.WebMessageReceived += RestoreHandler;
        try
        {
            var snapshotSize = state.Snapshot.Size;
            var accepted = await webView.ExecuteScriptAsync(
                $"window.trayTerminal.restoreState("
                + $"{JsonSerializer.Serialize(state.Snapshot.Sequence.ToString())},{snapshot},"
                + $"{JsonSerializer.Serialize(state.LatestSequence.ToString())},{replayPayload},"
                + $"{snapshotSize.Columns},{snapshotSize.Rows},"
                + $"{JsonSerializer.Serialize(restoreId)});");
            if (!string.Equals(accepted, "true", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The terminal renderer rejected the authoritative state.");
            }

            await restored.Task.WaitAsync(
                InitializationTimeout,
                _lifetimeCts.Token);
        }
        finally
        {
            restoreCoreWebView2.WebMessageReceived -= RestoreHandler;
        }

        Columns = state.Snapshot.Size.Columns;
        Rows = state.Snapshot.Size.Rows;
        var currentSize = _session.CurrentSize;
        if (currentSize != state.Snapshot.Size)
        {
            await ApplyAuthoritativeSizeCoreAsync(webView, currentSize);
            Columns = currentSize.Columns;
            Rows = currentSize.Rows;
        }
        Interlocked.Exchange(ref _outputQueueOverflow, 0);
    }

    private async Task RecoverLogicalStateAsync(string reason)
    {
        if (_disposed
            || Interlocked.Exchange(ref _logicalRecoveryScheduled, 1) != 0)
        {
            return;
        }

        try
        {
            if (_webView is { } webView)
            {
                await RecoverWebViewAsync(
                    webView,
                    reason,
                    invalidateEnvironment: false);
            }
        }
        catch (Exception exception)
        {
            if (!_disposed)
            {
                _logger.Error(exception, "Logical terminal state recovery failed.");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _logicalRecoveryScheduled, 0);
        }
    }

    private async Task ScheduleLogicalRecoveryAsync(string reason)
    {
        try
        {
            await Dispatcher.InvokeAsync(
                () => RecoverLogicalStateAsync(reason)).Task.Unwrap();
        }
        catch (Exception exception)
        {
            if (!_disposed)
            {
                _logger.Error(exception, "Failed to schedule logical terminal recovery.");
            }
        }
    }

    private async Task RequestLocalSizeEstimateFromWebViewAsync(WebView2 webView)
    {
        try
        {
            await RequestLocalSizeEstimateCoreAsync(webView);
        }
        catch (Exception exception) when (IsBrowserDisconnected(exception))
        {
            await RecoverWebViewAsync(
                webView,
                "local viewport size request was disconnected",
                exception);
        }
        catch (Exception exception)
        {
            if (!_disposed)
            {
                _logger.Error(exception, "Failed to request local viewport size.");
            }
        }
    }

    private static async Task ApplyAuthoritativeSizeCoreAsync(
        WebView2 webView,
        TerminalSize size)
    {
        await webView.ExecuteScriptAsync(
            $"window.trayTerminal.resizeAuthoritative("
            + $"{size.Columns},{size.Rows});");
    }

    private static async Task RequestLocalSizeEstimateCoreAsync(WebView2 webView)
    {
        await webView.ExecuteScriptAsync(
            "window.trayTerminal.requestSizeEstimate();");
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

    public async Task<BackgroundImageUpdateResult> SetBackgroundImageAsync(
        string? imagePath,
        int cover = 0)
    {
        try
        {
            await _backgroundUpdateGate.WaitAsync(_lifetimeCts.Token);
        }
        catch (OperationCanceledException) when (_disposed)
        {
            return BackgroundImageUpdateResult.Failure("终端标签已关闭，壁纸重载已取消。");
        }

        try
        {
            return await SetBackgroundImageCoreAsync(imagePath, cover);
        }
        finally
        {
            _backgroundUpdateGate.Release();
        }
    }

    private async Task<BackgroundImageUpdateResult> SetBackgroundImageCoreAsync(
        string? imagePath,
        int cover)
    {
        string? dataUri;
        try
        {
            dataUri = await LoadBackgroundDataUriAsync(
                imagePath,
                _lifetimeCts.Token);
        }
        catch (BackgroundImageTooLargeException exception)
        {
            return BackgroundImageUpdateResult.Failure(exception.Message);
        }
        catch (OperationCanceledException) when (_disposed)
        {
            return BackgroundImageUpdateResult.Failure("终端标签已关闭，壁纸重载已取消。");
        }
        catch (Exception exception) when (IsBackgroundReadException(exception))
        {
            _logger.Error(exception, $"Failed to read terminal background '{imagePath}'.");
            return BackgroundImageUpdateResult.Failure($"无法读取壁纸：{exception.Message}");
        }

        if (_disposed)
        {
            return BackgroundImageUpdateResult.Failure("终端标签已关闭，无法更新壁纸。");
        }

        var normalizedCover = Math.Clamp(cover, 0, 100);
        var webView = _webView;
        if (_ready && webView is not null)
        {
            try
            {
                await SetBackgroundDataUriAsync(
                    webView,
                    dataUri,
                    normalizedCover,
                    _lifetimeCts.Token);
            }
            catch (Exception exception) when (IsBrowserDisconnected(exception))
            {
                if (!await RecoverWebViewAsync(
                        webView,
                        "background update was disconnected",
                        exception))
                {
                    return BackgroundImageUpdateResult.Failure(
                        "终端视图当前不可用，未能更新壁纸。");
                }

                // Hidden tabs can defer recovery. In that case the prepared
                // data URI is committed below and applied when the view returns.
                if (_ready && _webView is { } recoveredWebView)
                {
                    try
                    {
                        await SetBackgroundDataUriAsync(
                            recoveredWebView,
                            dataUri,
                            normalizedCover,
                            _lifetimeCts.Token);
                    }
                    catch (Exception recoveryException)
                    {
                        if (recoveryException is TimeoutException)
                        {
                            await RecoverWebViewAsync(
                                recoveredWebView,
                                "background update timed out after recovery",
                                recoveryException,
                                invalidateEnvironment: false);
                        }

                        if (!_disposed)
                        {
                            _logger.Error(
                                recoveryException,
                                "Failed to apply terminal background after WebView2 recovery.");
                        }

                        return BackgroundImageUpdateResult.Failure(
                            $"无法将壁纸应用到终端视图：{recoveryException.Message}");
                    }
                }
            }
            catch (TimeoutException exception)
            {
                await RecoverWebViewAsync(
                    webView,
                    "background update timed out",
                    exception,
                    invalidateEnvironment: false);
                return BackgroundImageUpdateResult.Failure(
                    "应用壁纸超时，已恢复原壁纸。");
            }
            catch (Exception exception)
            {
                if (!_disposed)
                {
                    _logger.Error(exception, "Failed to apply terminal background.");
                }

                return BackgroundImageUpdateResult.Failure(
                    $"无法将壁纸应用到终端视图：{exception.Message}");
            }
        }

        // Commit only after reading and any immediate renderer update succeed.
        // A failed reload therefore leaves the previous wallpaper authoritative.
        if (_disposed)
        {
            return BackgroundImageUpdateResult.Failure(
                "终端标签已关闭，无法更新壁纸。");
        }

        _backgroundDataUri = dataUri;
        _backgroundCover = normalizedCover;
        return BackgroundImageUpdateResult.Success();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsVisibleChanged -= OnIsVisibleChanged;
        _session.SizeChanged -= OnSessionSizeChanged;
        _lifetimeCts.Cancel();
        foreach (var source in _pendingBatches.Values)
        {
            source.TrySetCanceled();
        }
        _pendingBatches.Clear();
        _outputQueue.Writer.TryComplete();
        _backgroundDataUri = null;
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
                _ = RecoverLogicalStateAsync(
                    $"renderer process exited ({e.Reason}, exit code {e.ExitCode})");
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
                            await RestoreAuthoritativeStateAsync(webView);
                            await SetFontSizeAsync(webView, _fontSize);
                            await SetBackgroundDataUriAsync(
                                webView,
                                _backgroundDataUri,
                                _backgroundCover,
                                _lifetimeCts.Token);
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

                case "interrupt":
                    await _session.TryInterruptAsync();
                    break;

                case "batchApplied":
                case "batchRejected":
                    if (root.TryGetProperty("batchId", out var batchId)
                        && batchId.GetString() is { } id
                        && _pendingBatches.TryGetValue(id, out var batchSource))
                    {
                        batchSource.TrySetResult(
                            typeProperty.GetString() == "batchApplied");
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
                        await _session.RequestResizeAsync(
                            _terminalClientId,
                            Columns,
                            Rows);
                    }
                    break;

                case "stateGap":
                    _logger.Warn(
                        "Terminal renderer detected an output sequence gap; "
                        + "rebuilding it from the authoritative checkpoint.");
                    await RecoverLogicalStateAsync(
                        "terminal output sequence gap");
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

    private static async Task SetFontSizeAsync(WebView2 webView, int fontSize)
    {
        await webView.ExecuteScriptAsync($"window.trayTerminal.setFontSize({fontSize});");
    }

    private static async Task<string?> LoadBackgroundDataUriAsync(
        string? imagePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }

        await using var input = new FileStream(
            imagePath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = BackgroundReadBufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
        if (input.Length > MaxBackgroundImageBytes)
        {
            throw new BackgroundImageTooLargeException();
        }

        var initialCapacity = (int)Math.Min(
            input.Length,
            MaxBackgroundImageBytes);
        using var content = new MemoryStream(initialCapacity);
        var buffer = new byte[BackgroundReadBufferSize];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            // Recheck while reading because a file can grow after Length was
            // sampled. Never retain more source bytes than the documented cap.
            var requiredLength = content.Length + read;
            if (requiredLength > MaxBackgroundImageBytes)
            {
                throw new BackgroundImageTooLargeException();
            }

            if (requiredLength > content.Capacity)
            {
                content.Capacity = (int)Math.Min(
                    MaxBackgroundImageBytes,
                    Math.Max(
                        requiredLength,
                        Math.Max(
                            BackgroundReadBufferSize,
                            content.Capacity * 2L)));
            }

            content.Write(buffer, 0, read);
        }

        var base64 = Convert.ToBase64String(
            content.GetBuffer(),
            0,
            checked((int)content.Length));
        var mime = Path.GetExtension(imagePath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            _ => "image/png"
        };
        return $"data:{mime};base64,{base64}";
    }

    private static async Task SetBackgroundDataUriAsync(
        WebView2 webView,
        string? dataUri,
        int cover,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(dataUri);
        await webView.ExecuteScriptAsync(
                $"window.trayTerminal.setBackground({payload}, {Math.Clamp(cover, 0, 100)});")
            .WaitAsync(InitializationTimeout, cancellationToken);
    }

    private static bool IsBackgroundReadException(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException;
    }

    private sealed class BackgroundImageTooLargeException : IOException
    {
        public BackgroundImageTooLargeException()
            : base("壁纸文件超过 32 MiB 上限，已保留当前壁纸。")
        {
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
