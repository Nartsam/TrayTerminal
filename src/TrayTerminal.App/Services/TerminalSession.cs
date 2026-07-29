using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using TrayTerminal.App.Dialogs;
using TrayTerminal.Shared;
using TrayTerminal.Shared.Ipc;
using TrayTerminal.Shared.Terminal;

namespace TrayTerminal.App.Services;

public sealed class TerminalSession : IAsyncDisposable
{
    private const int InputChunkSize = 64 * 1024;
    private const int MaxQueuedInputOperations = 64;
    private const int MaxInputOperationBytes = 4 * 1024 * 1024;
    private const int MaxQueuedInputBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan KillSendTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReaderStopTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InputAckTimeout = TimeSpan.FromSeconds(30);
    private const int MaxMissedHeartbeats = 3;

    private readonly NewTerminalRequest _request;
    private readonly PortablePaths _paths;
    private readonly FileLogger _logger;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _inputGate = new(1, 1);
    private readonly SemaphoreSlim _resizeGate = new(1, 1);
    private readonly object _subscribeLock = new();
    private readonly object _sizeLock = new();
    private readonly object _inputQueueLock = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TerminalStateHub _stateHub = new();
    private readonly TerminalSizeCoordinator _sizeCoordinator = new();
    private readonly InputAckRegistry _pendingInputs = new();
    private NamedPipeServerStream? _pipe;
    private Process? _hostProcess;
    private CancellationTokenSource? _cts;
    private Task? _readerTask;
    private Task? _heartbeatTask;
    private bool _disposed;
    private int _disposeStarted;
    private int _terminated;
    private long _lastResponseTicks;
    private long _nextInputRequestId;
    private long _latestSizeRevision;
    private int _queuedInputOperations;
    private int _queuedInputBytes;

    public TerminalSession(NewTerminalRequest request, PortablePaths paths, FileLogger logger)
    {
        _request = request;
        _paths = paths;
        _logger = logger;
    }

    public event EventHandler<TerminalOutput>? OutputReceived;

    public event EventHandler<TerminalSizeUpdate>? SizeChanged;

    public event EventHandler<int>? Exited;

    public event EventHandler<string>? Failed;

    public event EventHandler? Terminated;

    public bool IsRunning { get; private set; }

    public bool IsAdministrator => _request.RunAsAdministrator;

    public Task Completion => _completion.Task;

    /// <summary>
    /// Thread-safe subscribe to output events. The returned token must be disposed
    /// to unsubscribe; otherwise the handler keeps the session alive.
    /// </summary>
    public IDisposable SubscribeOutput(EventHandler<TerminalOutput> handler)
    {
        lock (_subscribeLock)
        {
            OutputReceived += handler;
        }

        return new OutputSubscription(this, handler);
    }

    private void UnsubscribeOutput(EventHandler<TerminalOutput> handler)
    {
        lock (_subscribeLock)
        {
            OutputReceived -= handler;
        }
    }

    private sealed class OutputSubscription : IDisposable
    {
        private TerminalSession? _session;
        private EventHandler<TerminalOutput>? _handler;

        public OutputSubscription(TerminalSession session, EventHandler<TerminalOutput> handler)
        {
            _session = session;
            _handler = handler;
        }

        public void Dispose()
        {
            var handler = _handler;
            _handler = null;
            if (handler is not null)
            {
                Interlocked.Exchange(ref _session, null)?.UnsubscribeOutput(handler);
            }
        }
    }

    public async Task StartAsync(int columns, int rows)
    {
        if (IsRunning)
        {
            return;
        }

        if (!File.Exists(_paths.HostExecutablePath))
        {
            throw new FileNotFoundException("TrayTerminal.Host.exe was not found beside the main executable.", _paths.HostExecutablePath);
        }

        var pipeName = $"TrayTerminal-{Guid.NewGuid():N}";
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        // Admin tabs run in a separately elevated Host, so the random nonce proves that
        // the process connecting to this pipe is the one we just launched.
        _pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            StartHostProcess(pipeName, nonce, columns, rows);
            await _pipe.WaitForConnectionAsync(_cts.Token);
            var hello = await IpcProtocol.ReadAsync(_pipe, _cts.Token);
            if (hello is null || hello.Value.Type != IpcMessageType.Hello || IpcProtocol.DecodeText(hello.Value.Payload) != nonce)
            {
                throw new InvalidDataException("Terminal host failed IPC handshake.");
            }

            _cts.Dispose();
            _cts = new CancellationTokenSource();
            IsRunning = true;
            _lastResponseTicks = Environment.TickCount64;
            _readerTask = Task.Run(() => ReadLoopAsync(_cts.Token));
            _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
            var currentSize = CurrentSize;
            if (currentSize.Columns != columns || currentSize.Rows != rows)
            {
                await SendAsync(
                    new IpcMessage(
                        IpcMessageType.Resize,
                        IpcProtocol.EncodeResize(
                            currentSize.Columns,
                            currentSize.Rows)),
                    _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            CleanupAfterFailedStart(killHost: true);
            CompleteTermination();
            throw new TimeoutException("Timed out waiting for terminal host to connect.");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            Failed?.Invoke(this, "已取消管理员授权");
            CleanupAfterFailedStart(killHost: false);
            CompleteTermination();
        }
        catch
        {
            CleanupAfterFailedStart(killHost: true);
            CompleteTermination();
            throw;
        }
    }

    public async Task SendInputAsync(string text)
    {
        await TrySendInputAsync(text);
    }

    public async Task<bool> TrySendInputAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        var inputByteCount = Encoding.UTF8.GetByteCount(text);
        if (inputByteCount > MaxInputOperationBytes)
        {
            return false;
        }

        lock (_inputQueueLock)
        {
            if (_queuedInputOperations >= MaxQueuedInputOperations
                || _queuedInputBytes > MaxQueuedInputBytes - inputByteCount)
            {
                return false;
            }

            _queuedInputOperations++;
            _queuedInputBytes += inputByteCount;
        }

        var gateEntered = false;
        try
        {
            await _inputGate.WaitAsync(GetSessionToken());
            gateEntered = true;

            var bytes = Encoding.UTF8.GetBytes(text);
            for (var offset = 0; offset < bytes.Length; offset += InputChunkSize)
            {
                var length = Math.Min(InputChunkSize, bytes.Length - offset);
                var requestId = Interlocked.Increment(ref _nextInputRequestId);
                if (requestId <= 0)
                {
                    return false;
                }

                if (!_pendingInputs.TryRegister(requestId, out var completion))
                {
                    return false;
                }

                try
                {
                    var payload = IpcProtocol.EncodeInput(
                        requestId,
                        bytes.AsSpan(offset, length));
                    if (!await SendAsync(new IpcMessage(IpcMessageType.Input, payload)))
                    {
                        return false;
                    }

                    using var ackCts = CancellationTokenSource.CreateLinkedTokenSource(
                        GetSessionToken());
                    ackCts.CancelAfter(InputAckTimeout);
                    if (!await completion.WaitAsync(ackCts.Token))
                    {
                        return false;
                    }
                }
                finally
                {
                    _pendingInputs.Remove(requestId);
                }
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            if (gateEntered)
            {
                try
                {
                    _inputGate.Release();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            lock (_inputQueueLock)
            {
                _queuedInputOperations--;
                _queuedInputBytes -= inputByteCount;
            }
        }
    }

    public TerminalSize CurrentSize
    {
        get
        {
            lock (_sizeLock)
            {
                return _sizeCoordinator.CurrentSize;
            }
        }
    }

    public bool HasRecoverableState => _stateHub.HasRecoverableState;

    public Guid RegisterTerminalClient(
        TerminalClientKind kind,
        bool isVisible,
        int columns,
        int rows)
    {
        var clientId = Guid.NewGuid();
        TerminalSizeUpdate update;
        lock (_sizeLock)
        {
            update = _sizeCoordinator.Register(
                clientId,
                kind,
                isVisible,
                columns,
                rows);
            _latestSizeRevision = update.Revision;
        }

        _ = ApplySizeUpdateAsync(update);
        return clientId;
    }

    public Task RequestResizeAsync(Guid clientId, int columns, int rows)
    {
        TerminalSizeUpdate update;
        lock (_sizeLock)
        {
            update = _sizeCoordinator.RequestResize(clientId, columns, rows);
            _latestSizeRevision = update.Revision;
        }

        return ApplySizeUpdateAsync(update);
    }

    public Task SetLocalClientVisibilityAsync(
        Guid clientId,
        bool isVisible,
        int columns,
        int rows)
    {
        TerminalSizeUpdate update;
        lock (_sizeLock)
        {
            update = _sizeCoordinator.SetLocalVisibility(
                clientId,
                isVisible,
                columns,
                rows);
            _latestSizeRevision = update.Revision;
        }

        return ApplySizeUpdateAsync(update);
    }

    public Task UnregisterTerminalClientAsync(Guid clientId)
    {
        TerminalSizeUpdate update;
        lock (_sizeLock)
        {
            update = _sizeCoordinator.Unregister(clientId);
            _latestSizeRevision = update.Revision;
        }

        return ApplySizeUpdateAsync(update);
    }

    public bool IsSizeOwner(Guid clientId)
    {
        lock (_sizeLock)
        {
            return _sizeCoordinator.OwnerId == clientId;
        }
    }

    public TerminalSizeUpdate GetCurrentSizeUpdate()
    {
        lock (_sizeLock)
        {
            return new TerminalSizeUpdate(
                _sizeCoordinator.CurrentSize,
                _sizeCoordinator.OwnerId,
                SizeChanged: false,
                OwnerChanged: false,
                Revision: _sizeCoordinator.Revision);
        }
    }

    public bool TryOpenRemoteStream(out TerminalStateSubscription? subscription)
    {
        lock (_sizeLock)
        {
            if (!_stateHub.TrySubscribe(out subscription)
                || subscription is null)
            {
                return false;
            }

            if (subscription.InitialState.Snapshot.Size
                == _sizeCoordinator.CurrentSize)
            {
                return true;
            }

            subscription.Dispose();
            subscription = null;
            return false;
        }
    }

    public bool TryGetRecoveryState(out TerminalInitialState? state)
    {
        lock (_sizeLock)
        {
            if (!_stateHub.TryGetRecoveryState(out state)
                || state is null
                || state.Snapshot.Size != _sizeCoordinator.CurrentSize)
            {
                state = null;
                return false;
            }

            return true;
        }
    }

    public bool TryAcceptSnapshot(
        long sequence,
        byte[] snapshot,
        int columns,
        int rows)
    {
        var snapshotSize = TerminalSize.Clamp(columns, rows);
        lock (_sizeLock)
        {
            if (snapshotSize != _sizeCoordinator.CurrentSize)
            {
                return false;
            }

            return _stateHub.TryAcceptSnapshot(
                sequence,
                snapshot,
                snapshotSize);
        }
    }

    private async Task ApplySizeUpdateAsync(TerminalSizeUpdate update)
    {
        if (!update.ShouldBroadcast)
        {
            return;
        }

        try
        {
            await _resizeGate.WaitAsync(GetSessionToken());
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (update.Revision != Volatile.Read(ref _latestSizeRevision))
            {
                return;
            }

            if (update.SizeChanged)
            {
                _stateHub.InvalidateCheckpoint();
            }

            if (IsRunning)
            {
                var sent = await SendAsync(
                    new IpcMessage(
                        IpcMessageType.Resize,
                        IpcProtocol.EncodeResize(
                            update.Size.Columns,
                            update.Size.Rows)));
                if (!sent)
                {
                    return;
                }
            }

            var handler = SizeChanged;
            handler?.Invoke(this, update);
        }
        catch (Exception exception)
        {
            if (!_disposed)
            {
                _logger.Error(exception, "Failed to apply terminal size ownership update.");
            }
        }
        finally
        {
            try
            {
                _resizeGate.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        IsRunning = false;

        try
        {
            using var killCts = new CancellationTokenSource(KillSendTimeout);
            await SendAsync(IpcMessage.Empty(IpcMessageType.Kill), killCts.Token);
        }
        catch
        {
        }

        _disposed = true;
        _cts?.Cancel();

        try
        {
            if (_hostProcess is not null && !_hostProcess.HasExited)
            {
                _hostProcess.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }

        try
        {
            _pipe?.Dispose();
        }
        catch
        {
        }

        if (_readerTask is not null)
        {
            try
            {
                await _readerTask.WaitAsync(ReaderStopTimeout);
            }
            catch
            {
            }
        }

        if (_heartbeatTask is not null)
        {
            try
            {
                await _heartbeatTask.WaitAsync(ReaderStopTimeout);
            }
            catch
            {
            }
        }

        _hostProcess?.Dispose();
        _stateHub.Dispose();
        _cts?.Dispose();
        _sendGate.Dispose();
        _inputGate.Dispose();
        _resizeGate.Dispose();
        CompleteTermination();
    }

    public void KillForShutdown()
    {
        IsRunning = false;
        try
        {
            _cts?.Cancel();
            if (_hostProcess is not null && !_hostProcess.HasExited)
            {
                _hostProcess.Kill(entireProcessTree: true);
            }

            _pipe?.Dispose();
        }
        catch
        {
        }

        CompleteTermination();
    }

    private void CleanupAfterFailedStart(bool killHost)
    {
        try
        {
            _cts?.Cancel();
            if (killHost && _hostProcess is not null && !_hostProcess.HasExited)
            {
                _hostProcess.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }

        try
        {
            _pipe?.Dispose();
        }
        catch
        {
        }

        _hostProcess?.Dispose();
        _hostProcess = null;
        _pipe = null;
        _cts?.Dispose();
        _cts = null;
    }

    private void StartHostProcess(string pipeName, string nonce, int columns, int rows)
    {
        var args = BuildHostArguments(pipeName, nonce, columns, rows);
        var startInfo = new ProcessStartInfo
        {
            FileName = _paths.HostExecutablePath,
            Arguments = args,
            WorkingDirectory = _paths.BaseDirectory,
            UseShellExecute = _request.RunAsAdministrator,
            CreateNoWindow = true
        };

        if (_request.RunAsAdministrator)
        {
            startInfo.Verb = "runas";
        }

        _hostProcess = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start terminal host.");
        _logger.Info($"Started host process {_hostProcess.Id} for {_request.Profile.DisplayName}.");
    }

    private string BuildHostArguments(string pipeName, string nonce, int columns, int rows)
    {
        // Base64 keeps executable paths, arguments, and working directories safe across
        // ProcessStartInfo argument parsing, especially when UAC relaunches the Host.
        return string.Join(
            ' ',
            "--pipe",
            pipeName,
            "--nonce",
            nonce,
            "--exe",
            Encode(_request.Profile.ExecutablePath),
            "--args",
            Encode(_request.Profile.Arguments),
            "--cwd",
            Encode(_request.Profile.WorkingDirectory),
            "--cols",
            columns.ToString(),
            "--rows",
            rows.ToString());
    }

    private static string Encode(string value) => value.Length == 0 ? "." : Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        if (_pipe is null)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await IpcProtocol.ReadAsync(_pipe, cancellationToken);
                if (message is null)
                {
                    break;
                }

                Interlocked.Exchange(ref _lastResponseTicks, Environment.TickCount64);

                switch (message.Value.Type)
                {
                    case IpcMessageType.Output:
                    {
                        var output = _stateHub.Publish(message.Value.Payload);
                        var handler = OutputReceived;
                        handler?.Invoke(this, output);
                        break;
                    }

                    case IpcMessageType.Exit:
                        IsRunning = false;
                        Exited?.Invoke(this, IpcProtocol.DecodeExit(message.Value.Payload));
                        return;

                    case IpcMessageType.Error:
                        Failed?.Invoke(this, IpcProtocol.DecodeText(message.Value.Payload));
                        break;

                    case IpcMessageType.Heartbeat:
                        break;

                    case IpcMessageType.InputAck:
                    {
                        var requestId = IpcProtocol.DecodeRequestId(message.Value.Payload);
                        _pendingInputs.TryAcknowledge(requestId);
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (!_disposed)
            {
                _logger.Error(exception, "Terminal session read loop failed.");
                Failed?.Invoke(this, exception.Message);
            }
        }
        finally
        {
            IsRunning = false;
            CompleteTermination();
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var missed = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, cancellationToken);
                if (!IsRunning) break;

                var before = Interlocked.Read(ref _lastResponseTicks);
                await SendAsync(IpcMessage.Empty(IpcMessageType.Heartbeat), cancellationToken);

                await Task.Delay(HeartbeatTimeout, cancellationToken);

                var after = Interlocked.Read(ref _lastResponseTicks);
                if (after > before)
                {
                    missed = 0;
                    continue;
                }

                missed++;
                _logger.Error($"Host missed heartbeat ({missed}/{MaxMissedHeartbeats}).");
                if (missed >= MaxMissedHeartbeats)
                {
                    _logger.Error("Host unresponsive, terminating session.");
                    IsRunning = false;
                    Failed?.Invoke(this, "终端宿主进程无响应，已终止会话");
                    _cts?.Cancel();
                    try
                    {
                        if (_hostProcess is not null && !_hostProcess.HasExited)
                            _hostProcess.Kill(entireProcessTree: true);
                    }
                    catch { }
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!_disposed)
                _logger.Error(exception, "Heartbeat loop failed.");
        }
    }

    private async Task<bool> SendAsync(IpcMessage message, CancellationToken cancellationToken = default)
    {
        if (_disposed || _pipe is null || !_pipe.IsConnected)
        {
            return false;
        }

        using var linkedCts = CreateLinkedSessionToken(cancellationToken);
        var effectiveToken = linkedCts?.Token
            ?? (cancellationToken.CanBeCanceled ? cancellationToken : GetSessionToken());

        try
        {
            // Wait with the session token so DisposeAsync's Cancel releases queued
            // senders — SemaphoreSlim.Dispose alone never wakes pending waiters and
            // would leak them (and their captured payloads) forever.
            await _sendGate.WaitAsync(effectiveToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        try
        {
            await IpcProtocol.WriteAsync(_pipe, message, effectiveToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (InvalidDataException exception)
        {
            _logger.Error(exception, "IPC send was rejected.");
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            try
            {
                _sendGate.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private CancellationTokenSource? CreateLinkedSessionToken(CancellationToken cancellationToken)
    {
        var sessionToken = GetSessionToken();
        if (sessionToken.CanBeCanceled && cancellationToken.CanBeCanceled)
        {
            return CancellationTokenSource.CreateLinkedTokenSource(sessionToken, cancellationToken);
        }

        return null;
    }

    private CancellationToken GetSessionToken()
    {
        try
        {
            return _cts?.Token ?? CancellationToken.None;
        }
        catch (ObjectDisposedException)
        {
            return CancellationToken.None;
        }
    }

    private void CompleteTermination()
    {
        if (Interlocked.Exchange(ref _terminated, 1) != 0)
        {
            return;
        }

        _pendingInputs.FailAll();

        _completion.TrySetResult();

        var handler = Terminated;
        if (handler is null)
        {
            return;
        }

        foreach (EventHandler callback in handler.GetInvocationList())
        {
            try
            {
                callback(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Terminal termination event handler failed.");
            }
        }
    }
}
