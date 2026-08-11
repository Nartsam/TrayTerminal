using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TrayTerminal.Shared;
using TrayTerminal.Shared.Terminal;

namespace TrayTerminal.App.Services;

public sealed record TerminalAuthorityCommit(
    long Sequence,
    byte[]? Snapshot,
    long? SnapshotSequence,
    TerminalSize? SnapshotSize,
    TerminalSize Size);

/// <summary>
/// Owns one process-level WebView2 control and one independent headless xterm
/// engine per terminal session. Visible WebViews and remote browsers are replicas
/// of checkpoints produced here; neither can become the state authority.
/// </summary>
public sealed class TerminalAuthorityHost : IAsyncDisposable
{
    public const int ProtocolVersion = 3;
    public const int MaxSessions = 32;
    // During recovery every one of the at-most-32 sessions must be able to make
    // progress even if another renderer command never responds. This remains a
    // hard process bound and per-session gates preserve each session's ordering.
    private const int MaxConcurrentOperations = MaxSessions;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RecoveryTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RecoveryDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(2);

    private readonly Dispatcher _dispatcher;
    private readonly PortablePaths _paths;
    private readonly FileLogger _logger;
    private readonly WebView2EnvironmentManager _environmentManager;
    private readonly SemaphoreSlim _operationSlots = new(MaxConcurrentOperations, MaxConcurrentOperations);
    private readonly ConcurrentDictionary<Guid, AuthoritySessionState> _sessions = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<AuthorityResponsePayload>> _pending = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly AuthorityRecoveryBarrier _hostBarrier = new();
    private readonly object _recoveryLock = new();
    private readonly object _sessionLock = new();
    private WebView2? _webView;
    private CoreWebView2? _coreWebView2;
    private CoreWebView2Environment? _environment;
    private Task? _recoveryTask;
    private CancellationTokenSource? _recoveryCts;
    private Window? _authorityWindow;
    private Grid? _container;
    private long _nextOperationId;
    private int _probeDiagnostics;
    private int _disposed;

    public TerminalAuthorityHost(
        PortablePaths paths,
        FileLogger logger,
        WebView2EnvironmentManager environmentManager)
    {
        _dispatcher = System.Windows.Application.Current.Dispatcher;
        _paths = paths;
        _logger = logger;
        _environmentManager = environmentManager;
        _hostBarrier.StartInitialRecovery();
    }

    public event EventHandler<AuthoritySessionFailedEventArgs>? SessionFailed;

    public Task InitializeAsync() => EnsureHostAsync(_lifetimeCts.Token);

    internal async Task RunRealWebViewProbeAsync(
        CancellationToken cancellationToken = default)
    {
        var sessionId = Guid.NewGuid();
        var created = false;
        Volatile.Write(ref _probeDiagnostics, 1);
        try
        {
            await InitializeAsync().ConfigureAwait(false);
            var initialSize = new TerminalSize(80, 24);
            var createdCommit = await CreateSessionAsync(
                sessionId,
                initialSize,
                cancellationToken).ConfigureAwait(false);
            created = true;
            if (createdCommit.Sequence != 0 || createdCommit.Snapshot is null)
            {
                throw new InvalidDataException(
                    "Authority probe did not receive its initial checkpoint.");
            }

            var first = await ProcessOutputAsync(
                sessionId,
                1,
                "AUTHORITY_PROBE_FIRST\r\n"u8.ToArray(),
                cancellationToken).ConfigureAwait(false);
            if (first.Sequence != 1)
            {
                throw new InvalidDataException(
                    "Authority probe output did not advance sequence one.");
            }

            await Task.Delay(
                CheckpointInterval + TimeSpan.FromMilliseconds(100),
                cancellationToken).ConfigureAwait(false);
            var checkpoint = await ProcessOutputAsync(
                sessionId,
                2,
                "AUTHORITY_PROBE_CHECKPOINT\r\n"u8.ToArray(),
                cancellationToken).ConfigureAwait(false);
            if (checkpoint.Snapshot is null
                || checkpoint.SnapshotSequence != 2
                || !Encoding.UTF8.GetString(checkpoint.Snapshot)
                    .Contains("AUTHORITY_PROBE_CHECKPOINT", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Authority probe did not receive the post-write checkpoint.");
            }

            var restored = await SendCoreAsync(
                new AuthorityCommand
                {
                    Type = "restore",
                    SessionId = sessionId.ToString("N"),
                    Sequence = checkpoint.Sequence.ToString(CultureInfo.InvariantCulture),
                    Snapshot = Convert.ToBase64String(checkpoint.Snapshot),
                    Cols = checkpoint.Size.Columns,
                    Rows = checkpoint.Size.Rows,
                    Probe = true
                },
                cancellationToken,
                _hostBarrier.Generation,
                requireReady: true).ConfigureAwait(false);
            if (!restored.Ok || restored.Sequence != "2")
            {
                throw new InvalidDataException(
                    "Authority probe could not replace an engine from its checkpoint.");
            }

            var afterRestore = await ProcessOutputAsync(
                sessionId,
                3,
                "AUTHORITY_PROBE_AFTER_RESTORE\r\n"u8.ToArray(),
                cancellationToken).ConfigureAwait(false);
            if (afterRestore.Sequence != 3)
            {
                throw new InvalidDataException(
                    "Authority probe did not advance after same-session restore.");
            }
        }
        finally
        {
            if (created)
            {
                await RemoveSessionAsync(sessionId).ConfigureAwait(false);
            }
            Volatile.Write(ref _probeDiagnostics, 0);
        }
    }

    public AuthorityAvailability GetSessionAvailability(Guid sessionId)
    {
        return _sessions.TryGetValue(sessionId, out var state)
            ? state.Availability
            : AuthorityAvailability.Failed;
    }

    public async Task<TerminalAuthorityCommit> CreateSessionAsync(
        Guid sessionId,
        TerminalSize size,
        CancellationToken cancellationToken = default)
    {
        await EnsureHostAsync(cancellationToken).ConfigureAwait(false);
        var state = new AuthoritySessionState(size);
        lock (_sessionLock)
        {
            if (_sessions.Count >= MaxSessions)
            {
                throw new InvalidOperationException("Terminal session hard limit reached.");
            }

            if (!_sessions.TryAdd(sessionId, state))
            {
                throw new InvalidOperationException("Duplicate terminal authority session.");
            }
        }

        try
        {
            var response = await RunSessionOperationAsync(
                sessionId,
                state,
                "create",
                sequence: 0,
                data: null,
                size,
                checkpoint: true,
                cancellationToken).ConfigureAwait(false);
            var commit = UpdateCheckpoint(
                sessionId,
                state,
                response,
                size,
                replayEvent: null);
            state.Availability = AuthorityAvailability.Ready;
            return commit;
        }
        catch
        {
            _sessions.TryRemove(sessionId, out _);
            await CleanupOrphanedEngineAsync(sessionId, state).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<TerminalAuthorityCommit> ProcessOutputAsync(
        Guid sessionId,
        long sequence,
        byte[] data,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var state = GetSession(sessionId);
        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureExpectedSequence(state, sequence);
            var checkpoint = ShouldCheckpoint(state, data.Length);
            var response = await RunSessionOperationAsync(
                sessionId,
                state,
                "output",
                sequence,
                data,
                state.Size,
                checkpoint,
                cancellationToken).ConfigureAwait(false);
            return UpdateCheckpoint(
                sessionId,
                state,
                response,
                state.Size,
                new AuthorityReplayEvent(
                    "output",
                    sequence,
                    data,
                    state.Size));
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task<TerminalAuthorityCommit> ResizeAsync(
        Guid sessionId,
        long sequence,
        TerminalSize size,
        CancellationToken cancellationToken = default)
    {
        var state = GetSession(sessionId);
        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureExpectedSequence(state, sequence);
            var checkpoint = ShouldCheckpoint(state, dataLength: 0);
            var response = await RunSessionOperationAsync(
                sessionId,
                state,
                "resize",
                sequence,
                data: null,
                size,
                checkpoint,
                cancellationToken).ConfigureAwait(false);
            return UpdateCheckpoint(
                sessionId,
                state,
                response,
                size,
                new AuthorityReplayEvent(
                    "resize",
                    sequence,
                    [],
                    size));
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task RemoveSessionAsync(Guid sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var state))
        {
            return;
        }

        state.Availability = AuthorityAvailability.Failed;
        await state.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CleanupOrphanedEngineAsync(sessionId, state).ConfigureAwait(false);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private async Task CleanupOrphanedEngineAsync(
        Guid sessionId,
        AuthoritySessionState state)
    {
        try
        {
            using var cleanupCts = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCts.Token);
            cleanupCts.CancelAfter(OperationTimeout);
            await EnsureHostAsync(cleanupCts.Token).ConfigureAwait(false);
            var generation = _hostBarrier.Generation;
            var response = await SendCoreAsync(
                new AuthorityCommand
                {
                    Type = "dispose",
                    SessionId = sessionId.ToString("N"),
                    Sequence = state.Sequence.ToString(CultureInfo.InvariantCulture)
                },
                cleanupCts.Token,
                generation,
                requireReady: true).ConfigureAwait(false);
            if (!response.Ok)
            {
                throw new TerminalAuthorityException(
                    response.Reason ?? "authorityDisposeFailed");
            }
        }
        catch when (Volatile.Read(ref _disposed) == 0)
        {
            // A canceled create may already have allocated its JS engine. If a
            // dispose cannot be proven, rebuilding the single host is the only
            // way to guarantee that removed engines cannot accumulate forever.
            InvalidateHost("authority engine cleanup could not be verified");
        }
    }

    private AuthoritySessionState GetSession(Guid sessionId)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (!_sessions.TryGetValue(sessionId, out var state)
            || state.Availability == AuthorityAvailability.Failed)
        {
            throw new TerminalAuthorityException("stateUnavailable");
        }

        return state;
    }

    private static void EnsureExpectedSequence(AuthoritySessionState state, long sequence)
    {
        if (sequence <= 0 || sequence != state.Sequence + 1)
        {
            throw new TerminalAuthorityException("sequenceGap");
        }
    }

    private async Task<AuthorityResponsePayload> RunSessionOperationAsync(
        Guid sessionId,
        AuthoritySessionState state,
        string type,
        long sequence,
        byte[]? data,
        TerminalSize size,
        bool checkpoint,
        CancellationToken cancellationToken)
    {
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);
        operationCts.CancelAfter(OperationTimeout);
        var operationToken = operationCts.Token;
        var deadline = DateTime.UtcNow + RecoveryTimeout;
        try
        {
            while (true)
            {
                var attemptedGeneration = 0L;
                try
                {
                    await EnsureHostAsync(operationToken).ConfigureAwait(false);
                    attemptedGeneration = _hostBarrier.Generation;
                    var response = await SendCoreAsync(
                        new AuthorityCommand
                        {
                            Type = type,
                            SessionId = sessionId.ToString("N"),
                            Sequence = sequence.ToString(CultureInfo.InvariantCulture),
                            Data = data is null ? null : Convert.ToBase64String(data),
                            Cols = size.Columns,
                            Rows = size.Rows,
                            Checkpoint = checkpoint,
                            Probe = Volatile.Read(ref _probeDiagnostics) != 0
                        },
                        operationToken,
                        attemptedGeneration,
                        requireReady: true).ConfigureAwait(false);

                    if (!response.Ok)
                    {
                        throw new TerminalAuthorityException(
                            response.Reason ?? "authorityFailure");
                    }

                    return response;
                }
                catch (AuthorityHostUnavailableException) when (DateTime.UtcNow < deadline)
                {
                    // A late result from a superseded renderer is discarded, but
                    // must never invalidate the newer generation that replaced it.
                    if (attemptedGeneration == _hostBarrier.Generation)
                    {
                        InvalidateHost("authority operation lost its browser process");
                    }
                    await Task.Delay(RecoveryDelay, operationToken).ConfigureAwait(false);
                }
                catch (TerminalAuthorityException exception)
                {
                    FailSession(sessionId, state, exception.Message);
                    throw;
                }
                catch (TimeoutException)
                {
                    FailSession(sessionId, state, "authorityTimeout");
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
            && !_lifetimeCts.IsCancellationRequested)
        {
            FailSession(sessionId, state, "authorityTimeout");
            throw new TimeoutException(
                "Terminal authority operation exceeded 30 seconds.");
        }
    }

    private TerminalAuthorityCommit UpdateCheckpoint(
        Guid sessionId,
        AuthoritySessionState state,
        AuthorityResponsePayload response,
        TerminalSize size,
        AuthorityReplayEvent? replayEvent)
    {
        if (!long.TryParse(
                response.Sequence,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var sequence)
            || sequence < 0)
        {
            throw new TerminalAuthorityException("invalidAuthorityResponse");
        }

        byte[]? snapshot = null;
        long? snapshotSequence = null;
        TerminalSize? snapshotSize = null;
        if (response.Snapshot is not null)
        {
            if (!long.TryParse(
                    response.SnapshotSequence,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsedSnapshotSequence)
                || parsedSnapshotSequence < 0
                || parsedSnapshotSequence > sequence
                || response.SnapshotCols is not { } snapshotColumns
                || response.SnapshotRows is not { } snapshotRows)
            {
                throw new TerminalAuthorityException("invalidAuthorityResponse");
            }

            try
            {
                snapshot = Convert.FromBase64String(response.Snapshot);
            }
            catch (FormatException exception)
            {
                throw new TerminalAuthorityException(
                    "invalidAuthoritySnapshot",
                    exception);
            }

            if (snapshot.Length > TerminalStateHub.DefaultSnapshotByteLimit)
            {
                throw new TerminalAuthorityException("stateTooLarge");
            }

            snapshotSequence = parsedSnapshotSequence;
            snapshotSize = TerminalSize.Clamp(snapshotColumns, snapshotRows);
        }

        lock (state.Sync)
        {
            state.Sequence = sequence;
            state.Size = size;
            state.Initialized = true;
            if (snapshot is not null
                && snapshotSequence is { } checkpointSequence
                && snapshotSize is { } checkpointSize)
            {
                if (checkpointSequence < state.RecoverySequence)
                {
                    throw new TerminalAuthorityException(
                        "authorityCheckpointRegressed");
                }

                state.Snapshot = snapshot;
                state.RecoverySequence = checkpointSequence;
                state.RecoverySize = checkpointSize;
                state.Replay.RemoveAll(item => item.Sequence <= checkpointSequence);
                state.ReplayBytes = state.Replay.Sum(item => item.Data.Length);
                state.LastCheckpointTicks = Environment.TickCount64;
            }

            if (replayEvent is not null
                && snapshotSequence != replayEvent.Sequence)
            {
                if (state.Replay.Count >= TerminalStateHub.DefaultTailItemLimit
                    || state.ReplayBytes > TerminalStateHub.DefaultTailByteLimit
                        - replayEvent.Data.Length)
                {
                    var exception = new TerminalAuthorityException("stateTooLarge");
                    FailSession(sessionId, state, exception.Message);
                    throw exception;
                }

                state.Replay.Add(replayEvent);
                state.ReplayBytes += replayEvent.Data.Length;
            }

            if (replayEvent is null && snapshot is null)
            {
                throw new TerminalAuthorityException("invalidAuthorityResponse");
            }
        }

        return new TerminalAuthorityCommit(
            sequence,
            snapshot,
            snapshotSequence,
            snapshotSize,
            size);
    }

    private void FailSession(Guid sessionId, AuthoritySessionState state, string reason)
    {
        if (state.Availability == AuthorityAvailability.Failed)
        {
            return;
        }

        state.Availability = AuthorityAvailability.Failed;
        state.Snapshot = [];
        _logger.Error($"Terminal authority session {sessionId:N} failed: {reason}");
        SessionFailed?.Invoke(
            this,
            new AuthoritySessionFailedEventArgs(sessionId, reason));
    }

    private async Task EnsureHostAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_hostBarrier.Availability == AuthorityAvailability.Ready
                && _coreWebView2 is not null)
            {
                return;
            }

            StartRecoveryIfNeeded();
            try
            {
                await _hostBarrier.WaitReadyAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (AuthorityGenerationSupersededException)
            {
                continue;
            }
        }
    }

    private static bool ShouldCheckpoint(
        AuthoritySessionState state,
        int dataLength)
    {
        lock (state.Sync)
        {
            return Environment.TickCount64 - state.LastCheckpointTicks
                    >= CheckpointInterval.TotalMilliseconds
                || state.Replay.Count >= TerminalStateHub.DefaultTailItemLimit - 1
                || state.ReplayBytes
                    > TerminalStateHub.DefaultTailByteLimit - dataLength;
        }
    }

    private void StartRecoveryIfNeeded()
    {
        lock (_recoveryLock)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            if (_recoveryTask is { IsCompleted: false })
            {
                return;
            }

            if (_hostBarrier.Availability != AuthorityAvailability.Recovering)
            {
                _hostBarrier.BeginRecovery();
            }

            var generation = _hostBarrier.Generation;
            _recoveryCts?.Dispose();
            _recoveryCts = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCts.Token);
            _recoveryCts.CancelAfter(RecoveryTimeout);
            _recoveryTask = RecoverHostGenerationAsync(
                generation,
                _recoveryCts);
        }
    }

    private async Task RecoverHostGenerationAsync(
        long generation,
        CancellationTokenSource recoveryCts)
    {
        var cancellationToken = recoveryCts.Token;
        Exception? createFailure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested
                && generation == _hostBarrier.Generation)
            {
                try
                {
                    await CreateHostCoreAsync(generation, cancellationToken)
                        .ConfigureAwait(false);
                    createFailure = null;
                    break;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    if (generation != _hostBarrier.Generation)
                    {
                        return;
                    }

                    createFailure = exception;
                    DisposeCurrentWebView();
                    await Task.Delay(RecoveryDelay, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            if (_coreWebView2 is null)
            {
                throw new TimeoutException(
                    "The terminal authority WebView2 could not start within 30 seconds.",
                    createFailure);
            }

            var restoreTargets = _sessions
                .Where(pair => pair.Value.Initialized
                    && pair.Value.Availability != AuthorityAvailability.Failed)
                .ToArray();
            foreach (var pair in restoreTargets)
            {
                pair.Value.Availability = AuthorityAvailability.Recovering;
            }

            var restoreTasks = restoreTargets.ToDictionary(
                pair => pair.Key,
                pair => RestoreSessionCoreAsync(
                    pair.Key,
                    pair.Value,
                    generation,
                    cancellationToken));
            try
            {
                await Task.WhenAll(restoreTasks.Values).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }

            if (generation != _hostBarrier.Generation)
            {
                return;
            }

            foreach (var pair in restoreTargets)
            {
                if (restoreTasks[pair.Key].IsCompletedSuccessfully
                    && restoreTasks[pair.Key].Result)
                {
                    pair.Value.Availability = AuthorityAvailability.Ready;
                }
                else
                {
                    FailSession(pair.Key, pair.Value, "authorityRecoveryTimeout");
                }
            }

            if (_hostBarrier.TrySetReady(generation))
            {
                _logger.Info("Terminal authority WebView2 recovery completed.");
            }
        }
        catch (OperationCanceledException) when (recoveryCts.IsCancellationRequested)
        {
            if (generation == _hostBarrier.Generation)
            {
                foreach (var pair in _sessions)
                {
                    if (pair.Value.Availability == AuthorityAvailability.Recovering)
                    {
                        FailSession(pair.Key, pair.Value, "authorityRecoveryTimeout");
                    }
                }

                _hostBarrier.TrySetFailed(
                    generation,
                    new TimeoutException(
                        "The terminal authority did not recover within 30 seconds."));
            }
        }
        catch (Exception exception)
        {
            if (generation == _hostBarrier.Generation)
            {
                foreach (var pair in _sessions)
                {
                    if (pair.Value.Availability == AuthorityAvailability.Recovering)
                    {
                        FailSession(
                            pair.Key,
                            pair.Value,
                            $"authorityRestoreFailed: {exception.Message}");
                    }
                }

                _hostBarrier.TrySetFailed(generation, exception);
            }
        }
        finally
        {
            lock (_recoveryLock)
            {
                if (ReferenceEquals(_recoveryCts, recoveryCts))
                {
                    _recoveryTask = null;
                    _recoveryCts = null;
                }
            }

            recoveryCts.Dispose();
        }
    }

    private async Task CreateHostCoreAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.AuthorityTerminalHtmlPath))
        {
            throw new FileNotFoundException(
                "Terminal authority asset was not copied to the output directory.",
                _paths.AuthorityTerminalHtmlPath);
        }

        var ready = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var container = await EnsureAuthorityWindowAsync();
        var webView = await container.Dispatcher.InvokeAsync(() =>
        {
            var created = new WebView2
            {
                Width = 1,
                Height = 1,
                IsHitTestVisible = false,
                Focusable = false
            };
            container.Children.Add(created);
            return created;
        });

        var environment = await _environmentManager.GetEnvironmentAsync();
        await container.Dispatcher.InvokeAsync(async () =>
        {
            await webView.EnsureCoreWebView2Async(environment);
            if (generation != _hostBarrier.Generation)
            {
                container.Children.Remove(webView);
                webView.Dispose();
                throw new AuthorityHostUnavailableException();
            }
            var core = webView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.WebMessageReceived += OnWebMessageReceived;
            core.WebMessageReceived += ReadyHandler;
            core.ProcessFailed += OnProcessFailed;
            _webView = webView;
            _coreWebView2 = core;
            _environment = environment;
            webView.Source = new Uri(_paths.AuthorityTerminalHtmlPath);
        }).Task.Unwrap();

        void ReadyHandler(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                using var document = JsonDocument.Parse(args.WebMessageAsJson);
                var root = document.RootElement;
                if (root.TryGetProperty("version", out var version)
                    && version.GetInt32() == ProtocolVersion
                    && root.TryGetProperty("type", out var type))
                {
                    if (type.GetString() == "ready")
                    {
                        ready.TrySetResult();
                    }
                    else if (type.GetString() == "fatal")
                    {
                        ready.TrySetException(new InvalidOperationException(
                            root.GetProperty("reason").GetString()));
                    }
                }
            }
            catch (Exception exception)
            {
                ready.TrySetException(exception);
            }
        }

        try
        {
            await ready.Task.WaitAsync(OperationTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (_coreWebView2 is { } core)
            {
                core.WebMessageReceived -= ReadyHandler;
            }
        }
    }

    private async Task<Grid> EnsureAuthorityWindowAsync()
    {
        return await _dispatcher.InvokeAsync(() =>
        {
            if (_container is { } existing)
            {
                return existing;
            }

            var container = new Grid
            {
                Width = 1,
                Height = 1,
                IsHitTestVisible = false,
                Focusable = false
            };
            var window = new Window
            {
                Width = 1,
                Height = 1,
                Left = -32000,
                Top = -32000,
                Opacity = 0.01,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                Content = container
            };

            // This HWND must remain shown even while MainWindow is hidden to the
            // tray. Direct parser driving avoids renderer timers, while the live
            // offscreen WebView keeps IPC and crash recovery independent of tabs.
            window.Show();
            _authorityWindow = window;
            _container = container;
            return container;
        });
    }

    private async Task<bool> RestoreSessionCoreAsync(
        Guid sessionId,
        AuthoritySessionState state,
        long generation,
        CancellationToken cancellationToken)
    {
        AuthorityRecoverySnapshot recovery;
        lock (state.Sync)
        {
            recovery = new AuthorityRecoverySnapshot(
                state.RecoverySequence,
                state.Snapshot,
                state.RecoverySize,
                state.Replay.ToArray());
        }

        var type = recovery.Sequence == 0 && recovery.Snapshot.Length == 0
            ? "create"
            : "restore";
        try
        {
            var response = await SendCoreAsync(
                new AuthorityCommand
                {
                    Type = type,
                    SessionId = sessionId.ToString("N"),
                    Sequence = recovery.Sequence.ToString(CultureInfo.InvariantCulture),
                    Snapshot = type == "restore"
                        ? Convert.ToBase64String(recovery.Snapshot)
                        : null,
                    Cols = recovery.Size.Columns,
                    Rows = recovery.Size.Rows
                },
                cancellationToken,
                generation,
                requireReady: false).ConfigureAwait(false);
            if (!response.Ok)
            {
                FailSession(
                    sessionId,
                    state,
                    response.Reason ?? "authorityRestoreFailed");
                return false;
            }

            foreach (var replayEvent in recovery.Replay)
            {
                response = await SendCoreAsync(
                    new AuthorityCommand
                    {
                        Type = replayEvent.Type,
                        SessionId = sessionId.ToString("N"),
                        Sequence = replayEvent.Sequence.ToString(
                            CultureInfo.InvariantCulture),
                        Data = replayEvent.Type == "output"
                            ? Convert.ToBase64String(replayEvent.Data)
                            : null,
                        Cols = replayEvent.Size.Columns,
                        Rows = replayEvent.Size.Rows
                    },
                    cancellationToken,
                    generation,
                    requireReady: false).ConfigureAwait(false);
                if (!response.Ok)
                {
                    FailSession(
                        sessionId,
                        state,
                        response.Reason ?? "authorityReplayFailed");
                    return false;
                }
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (AuthorityHostUnavailableException)
        {
            return false;
        }
        catch (Exception exception)
        {
            FailSession(
                sessionId,
                state,
                $"authorityRestoreFailed: {exception.Message}");
            return false;
        }
    }

    private async Task<AuthorityResponsePayload> SendCoreAsync(
        AuthorityCommand command,
        CancellationToken cancellationToken,
        long generation,
        bool requireReady)
    {
        await _operationSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        var operationId = Interlocked.Increment(ref _nextOperationId);
        if (operationId <= 0 || operationId > 9_007_199_254_740_991)
        {
            _operationSlots.Release();
            throw new InvalidOperationException("Authority operation ID space exhausted.");
        }

        command.OperationId = operationId;
        var source = new TaskCompletionSource<AuthorityResponsePayload>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(operationId, source))
        {
            _operationSlots.Release();
            throw new InvalidOperationException("Duplicate authority operation ID.");
        }

        try
        {
            var core = _coreWebView2 ?? throw new AuthorityHostUnavailableException();
            var container = _container ?? throw new AuthorityHostUnavailableException();
            var json = JsonSerializer.Serialize(command, AuthorityJsonContext.Default.AuthorityCommand);
            try
            {
                await container.Dispatcher.InvokeAsync(() => core.PostWebMessageAsJson(json));
            }
            catch (Exception exception)
            {
                throw new AuthorityHostUnavailableException(exception);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCts.Token);
            timeoutCts.CancelAfter(OperationTimeout);
            try
            {
                var response = await source.Task.WaitAsync(timeoutCts.Token)
                    .ConfigureAwait(false);
                if (generation != _hostBarrier.Generation
                    || (requireReady && !_hostBarrier.IsCurrentReady(generation)))
                {
                    throw new AuthorityHostUnavailableException();
                }

                return response;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                && !_lifetimeCts.IsCancellationRequested)
            {
                throw new TimeoutException("Terminal authority operation exceeded 30 seconds.");
            }
        }
        finally
        {
            _pending.TryRemove(operationId, out _);
            _operationSlots.Release();
        }
    }

    private void OnWebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs args)
    {
        TaskCompletionSource<AuthorityResponsePayload>? responseSource = null;
        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var root = document.RootElement;
            var typeName = root.TryGetProperty("type", out var typeProperty)
                && typeProperty.ValueKind == JsonValueKind.String
                    ? typeProperty.GetString()
                    : null;
            if (typeName == "probeStage"
                && Volatile.Read(ref _probeDiagnostics) != 0)
            {
                var diagnosticOperation = root.TryGetProperty(
                        "operationId",
                        out var diagnosticOperationProperty)
                    && diagnosticOperationProperty.TryGetInt64(out var parsedOperation)
                        ? parsedOperation
                        : 0;
                var stage = root.TryGetProperty("stage", out var stageProperty)
                    ? stageProperty.GetString()
                    : "unknown";
                _logger.Info(
                    $"Authority probe operation {diagnosticOperation} stage: {stage}.");
                return;
            }

            if (typeName != "result")
            {
                return;
            }

            if (!root.TryGetProperty("operationId", out var operationIdProperty)
                || !operationIdProperty.TryGetInt64(out var operationId)
                || !_pending.TryGetValue(operationId, out var source))
            {
                if (Volatile.Read(ref _probeDiagnostics) != 0)
                {
                    _logger.Info(
                        "Authority probe ignored a result with no matching pending "
                        + $"operation (JSON length {args.WebMessageAsJson.Length}).");
                }
                return;
            }
            responseSource = source;
            if (!root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var parsedVersion)
                || parsedVersion != ProtocolVersion)
            {
                throw new InvalidDataException(
                    "Authority response has an invalid protocol version.");
            }

            var accepted = source.TrySetResult(AuthorityResponseProtocol.Parse(root));
            if (Volatile.Read(ref _probeDiagnostics) != 0)
            {
                _logger.Info(
                    $"Authority probe operation {operationId} result accepted: {accepted}.");
            }
        }
        catch (Exception exception)
        {
            _logger.Warn($"Ignored malformed authority response: {exception.Message}");
            responseSource?.TrySetException(new InvalidDataException(
                "The terminal authority returned a malformed response.",
                exception));
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
    {
        if (!ReferenceEquals(sender, _coreWebView2))
        {
            return;
        }

        if (args.ProcessFailedKind is not CoreWebView2ProcessFailedKind.BrowserProcessExited
            and not CoreWebView2ProcessFailedKind.RenderProcessExited)
        {
            return;
        }

        InvalidateHost(
            $"authority process failed ({args.ProcessFailedKind}, {args.Reason}, {args.ExitCode})");
    }

    private void InvalidateHost(string reason)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        lock (_recoveryLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _logger.Warn($"Terminal authority WebView2 invalidated: {reason}");
            _recoveryCts?.Cancel();
            var generation = _hostBarrier.RestartRecovery();
            foreach (var state in _sessions.Values)
            {
                if (state.Initialized
                    && state.Availability != AuthorityAvailability.Failed)
                {
                    state.Availability = AuthorityAvailability.Recovering;
                }
            }

            DisposeCurrentWebView();
            foreach (var source in _pending.Values)
            {
                source.TrySetException(new AuthorityHostUnavailableException());
            }

            _recoveryCts = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCts.Token);
            _recoveryCts.CancelAfter(RecoveryTimeout);
            _recoveryTask = RecoverHostGenerationAsync(
                generation,
                _recoveryCts);
        }
    }

    private void DisposeCurrentWebView()
    {
        var webView = _webView;
        var core = _coreWebView2;
        var container = _container;
        _webView = null;
        _coreWebView2 = null;
        if (core is not null)
        {
            try
            {
                core.WebMessageReceived -= OnWebMessageReceived;
                core.ProcessFailed -= OnProcessFailed;
            }
            catch
            {
            }
        }

        if (webView is null || container is null)
        {
            return;
        }

        void DisposeWebView()
        {
            container.Children.Remove(webView);
            webView.Dispose();
        }

        try
        {
            if (container.Dispatcher.CheckAccess())
            {
                DisposeWebView();
            }
            else
            {
                container.Dispatcher.Invoke(DisposeWebView);
            }
        }
        catch
        {
            // A shutting-down dispatcher already makes the controller unusable;
            // references were cleared above so no recovery can reuse it.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetimeCts.Cancel();
        foreach (var source in _pending.Values)
        {
            source.TrySetCanceled();
        }

        Task? recoveryTask;
        lock (_recoveryLock)
        {
            _recoveryCts?.Cancel();
            recoveryTask = _recoveryTask;
        }

        if (recoveryTask is not null)
        {
            try
            {
                await recoveryTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        try
        {
            foreach (var state in _sessions.Values)
            {
                await state.Gate.WaitAsync().ConfigureAwait(false);
                state.Gate.Release();
                state.Gate.Dispose();
            }
        }
        finally
        {
            _sessions.Clear();
            DisposeCurrentWebView();
            DisposeAuthorityWindow();
            _lifetimeCts.Dispose();
            _operationSlots.Dispose();
        }
    }

    private void DisposeAuthorityWindow()
    {
        var window = _authorityWindow;
        var container = _container;
        _authorityWindow = null;
        _container = null;
        if (window is null)
        {
            return;
        }

        void CloseWindow()
        {
            container?.Children.Clear();
            window.Content = null;
            window.Close();
        }

        try
        {
            if (_dispatcher.CheckAccess())
            {
                CloseWindow();
            }
            else
            {
                _dispatcher.Invoke(CloseWindow);
            }
        }
        catch
        {
            // Application shutdown may already have stopped the dispatcher.
            // Managed references are still cleared so the host cannot be reused.
        }
    }

    private sealed class AuthoritySessionState
    {
        public AuthoritySessionState(TerminalSize size)
        {
            Size = size;
            RecoverySize = size;
        }

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public object Sync { get; } = new();

        public long Sequence { get; set; }

        public byte[] Snapshot { get; set; } = [];

        public long RecoverySequence { get; set; }

        public TerminalSize RecoverySize { get; set; }

        public List<AuthorityReplayEvent> Replay { get; } = [];

        public int ReplayBytes { get; set; }

        public TerminalSize Size { get; set; }

        public bool Initialized { get; set; }

        public AuthorityAvailability Availability
        {
            get => (AuthorityAvailability)Volatile.Read(ref _availability);
            set => Volatile.Write(ref _availability, (int)value);
        }

        public long LastCheckpointTicks { get; set; } = Environment.TickCount64;

        private int _availability = (int)AuthorityAvailability.Recovering;
    }

    internal sealed class AuthorityCommand
    {
        public int Version { get; init; } = ProtocolVersion;

        public long OperationId { get; set; }

        public required string Type { get; init; }

        public required string SessionId { get; init; }

        public string? Sequence { get; init; }

        public string? Data { get; init; }

        public string? Snapshot { get; init; }

        public int? Cols { get; init; }

        public int? Rows { get; init; }

        public bool? Checkpoint { get; init; }

        public bool? Probe { get; init; }
    }

    private sealed record AuthorityReplayEvent(
        string Type,
        long Sequence,
        byte[] Data,
        TerminalSize Size);

    private sealed record AuthorityRecoverySnapshot(
        long Sequence,
        byte[] Snapshot,
        TerminalSize Size,
        AuthorityReplayEvent[] Replay);
}

public sealed record AuthoritySessionFailedEventArgs(Guid SessionId, string Reason);

public class TerminalAuthorityException : Exception
{
    public TerminalAuthorityException(string message)
        : base(message)
    {
    }

    public TerminalAuthorityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class AuthorityHostUnavailableException : Exception
{
    public AuthorityHostUnavailableException()
    {
    }

    public AuthorityHostUnavailableException(Exception innerException)
        : base("The terminal authority browser process is unavailable.", innerException)
    {
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(TerminalAuthorityHost.AuthorityCommand))]
internal partial class AuthorityJsonContext : JsonSerializerContext
{
}
