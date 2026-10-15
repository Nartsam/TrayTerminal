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
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan KillSendTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReaderStopTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OperationAckTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CloseCheckTimeout = TimeSpan.FromSeconds(1);
    private const int MaxMissedHeartbeats = 3;
    private const int ErrorCancelled = 1223;
    private static readonly byte[] DegradedRecoveryNotice = Encoding.UTF8.GetBytes(
        "\u001b[0m\u001b[33m[TrayTerminal] 状态同步已停用，显示已重置；终端进程仍在运行。\u001b[0m\r\n");

    private readonly NewTerminalRequest _request;
    private readonly PortablePaths _paths;
    private readonly FileLogger _logger;
    private readonly TerminalAuthorityHost _authority;
    private readonly Guid _authoritySessionId = Guid.NewGuid();
    private readonly SemaphoreSlim _controlSendGate = new(1, 1);
    private readonly SemaphoreSlim _inputGate = new(1, 1);
    private readonly SemaphoreSlim _resizeGate = new(1, 1);
    private readonly SemaphoreSlim _closeCheckGate = new(1, 1);
    private readonly object _subscribeLock = new();
    private readonly object _sizeLock = new();
    private readonly object _inputQueueLock = new();
    private readonly object _resizeRequestLock = new();
    private readonly object _sizeApplyLock = new();
    private readonly object _authorityRemovalLock = new();
    private readonly object _closeCheckLock = new();
    // Serializes event sequencing, delivery to local replicas, and the switch to
    // degraded mode, so a recovering replica can never observe a sequence whose
    // delivery has not been queued yet.
    private readonly object _commitLock = new();
    private readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _outputCompleted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TerminalStateHub _stateHub = new();
    private readonly TerminalSizeCoordinator _sizeCoordinator = new();
    private readonly InputAckRegistry _pendingInputs = new();
    private readonly Dictionary<long, TaskCompletionSource<bool>> _pendingResizes = new();
    private NamedPipeServerStream? _outputPipe;
    private NamedPipeServerStream? _controlPipe;
    private Process? _hostProcess;
    private CancellationTokenSource? _cts;
    private Task? _outputReaderTask;
    private Task? _controlReaderTask;
    private Task? _heartbeatTask;
    private bool _disposed;
    private int _disposeStarted;
    private int _terminated;
    private int _failed;
    private int _authorityCreated;
    private int _inputGeneration;
    private long _lastResponseTicks;
    private long _nextRequestId;
    private long _latestSizeRevision;
    private int _queuedInputOperations;
    private int _queuedInputBytes;
    private int _queuedInterrupts;
    private TerminalSizeUpdate? _pendingSizeUpdate;
    private TerminalSize _appliedSize = new(120, 30);
    private Guid? _appliedSizeOwner;
    private Task? _sizeApplyTask;
    private Task _authorityRemovalTask = Task.CompletedTask;
    private TaskCompletionSource<TerminalCloseAssessment>? _pendingCloseCheck;
    private TaskCompletionSource? _interruptDrainCompletion;
    private long _pendingCloseCheckRequestId;
    private int _inputFrozen;
    private int _authorityDegraded;
    private long _degradedSequence;

    public TerminalSession(
        NewTerminalRequest request,
        PortablePaths paths,
        FileLogger logger,
        TerminalAuthorityHost authority)
    {
        _request = request;
        _paths = paths;
        _logger = logger;
        _authority = authority;
        _authority.SessionFailed += OnAuthoritySessionFailed;
    }

    public event EventHandler<TerminalOutput>? OutputReceived;

    public event EventHandler<TerminalSizeUpdate>? SizeChanged;

    public event EventHandler<int>? Exited;

    public event EventHandler<string>? Failed;

    public event EventHandler? Terminated;

    /// <summary>
    /// Raised once when the authority can no longer prove this terminal's state.
    /// The terminal keeps running; only remote replicas are disconnected.
    /// </summary>
    public event EventHandler<string>? AuthorityDegraded;

    public bool IsRunning { get; private set; }

    public bool IsFailed => Volatile.Read(ref _failed) != 0;

    public bool IsAuthorityDegraded => Volatile.Read(ref _authorityDegraded) != 0;

    public string? FailureReason { get; private set; }

    public int? ExitCode { get; private set; }

    public bool OutputComplete { get; private set; }

    public bool IsAdministrator => _request.RunAsAdministrator && !ElevationDeclined;

    /// <summary>
    /// True when an administrator terminal fell back to normal privileges
    /// because the user declined the UAC prompt.
    /// </summary>
    public bool ElevationDeclined { get; private set; }

    public Task Completion => _completion.Task;

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

    public async Task StartAsync(int columns, int rows)
    {
        if (IsRunning)
        {
            return;
        }

        if (!File.Exists(_paths.HostExecutablePath))
        {
            throw new FileNotFoundException(
                "TrayTerminal.Host.exe was not found beside the main executable.",
                _paths.HostExecutablePath);
        }

        var initialSize = TerminalSize.Clamp(columns, rows);
        var authorityCommit = await CreateAuthoritySessionAsync(initialSize);
        if (authorityCommit.Snapshot is null
            || authorityCommit.SnapshotSequence != 0
            || !_stateHub.TryAcceptSnapshot(
                0,
                authorityCommit.Snapshot,
                initialSize))
        {
            await EnsureAuthorityRemovedAsync();
            throw new InvalidOperationException(
                "The terminal authority rejected its bootstrap checkpoint.");
        }

        var outputPipeName = $"TrayTerminal-Output-{Guid.NewGuid():N}";
        var controlPipeName = $"TrayTerminal-Control-{Guid.NewGuid():N}";
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _outputPipe = new NamedPipeServerStream(
            outputPipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        _controlPipe = new NamedPipeServerStream(
            controlPipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        try
        {
            StartHostProcess(
                outputPipeName,
                controlPipeName,
                nonce,
                initialSize.Columns,
                initialSize.Rows);
            // Start the deadline only now: time spent answering the UAC prompt
            // must not count toward the Host connection timeout.
            _cts = new CancellationTokenSource(ConnectTimeout);
            await Task.WhenAll(
                _outputPipe.WaitForConnectionAsync(_cts.Token),
                _controlPipe.WaitForConnectionAsync(_cts.Token));
            var hellos = await Task.WhenAll(
                IpcProtocol.ReadAsync(_outputPipe, _cts.Token),
                IpcProtocol.ReadAsync(_controlPipe, _cts.Token));
            if (hellos.Any(hello => hello is null
                    || hello.Value.Type != IpcMessageType.Hello
                    || IpcProtocol.DecodeText(hello.Value.Payload) != nonce))
            {
                throw new InvalidDataException("Terminal host failed dual-pipe IPC handshake.");
            }

            _cts.Dispose();
            _cts = new CancellationTokenSource();
            // Requests can change while authority/Host startup is awaiting. The
            // new ConPTY still starts at initialSize, not the latest requested size.
            _appliedSize = initialSize;
            IsRunning = true;
            _lastResponseTicks = Environment.TickCount64;
            _outputReaderTask = Task.Run(() => ReadOutputLoopAsync(_cts.Token));
            _controlReaderTask = Task.Run(() => ReadControlLoopAsync(_cts.Token));
            _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_cts.Token));

            await QueueSizeUpdate(GetCurrentSizeUpdate());
        }
        catch (OperationCanceledException)
        {
            await CleanupAfterFailedStartAsync(killHost: true);
            CompleteTermination();
            throw new TimeoutException("Timed out waiting for terminal host to connect.");
        }
        catch
        {
            await CleanupAfterFailedStartAsync(killHost: true);
            CompleteTermination();
            throw;
        }
    }

    private async Task<TerminalAuthorityCommit> CreateAuthoritySessionAsync(
        TerminalSize size)
    {
        var commit = await _authority.CreateSessionAsync(_authoritySessionId, size);
        Interlocked.Exchange(ref _authorityCreated, 1);
        return commit;
    }

    public Task SendInputAsync(string text) => TrySendInputAsync(text);

    public async Task<bool> TrySendInputAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        var generation = Volatile.Read(ref _inputGeneration);
        if (Volatile.Read(ref _inputFrozen) != 0)
        {
            return false;
        }

        var inputByteCount = Encoding.UTF8.GetByteCount(text);
        if (inputByteCount > MaxInputOperationBytes)
        {
            return false;
        }

        lock (_inputQueueLock)
        {
            // Keep one of the 64 total operation slots available for the
            // independent interrupt path, even when ordinary/paste input floods
            // the session.
            if (_queuedInputOperations + _queuedInterrupts
                    >= MaxQueuedInputOperations - 1
                || _queuedInputBytes > MaxQueuedInputBytes - inputByteCount)
            {
                return false;
            }

            _queuedInputOperations++;
            _queuedInputBytes += inputByteCount;
        }

        var gateEntered = false;
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(
            GetSessionToken(),
            cancellationToken);
        var operationToken = operationCts.Token;
        try
        {
            await _inputGate.WaitAsync(operationToken);
            gateEntered = true;
            if (Volatile.Read(ref _inputFrozen) != 0
                || generation != Volatile.Read(ref _inputGeneration))
            {
                return false;
            }

            var bytes = Encoding.UTF8.GetBytes(text);
            for (var offset = 0; offset < bytes.Length; offset += InputChunkSize)
            {
                if (Volatile.Read(ref _inputFrozen) != 0
                    || generation != Volatile.Read(ref _inputGeneration))
                {
                    return false;
                }

                var length = Math.Min(InputChunkSize, bytes.Length - offset);
                var requestId = NextRequestId();
                if (!_pendingInputs.TryRegister(requestId, out var completion))
                {
                    return false;
                }

                try
                {
                    var payload = IpcProtocol.EncodeInput(
                        requestId,
                        bytes.AsSpan(offset, length));
                    if (!await SendControlAsync(
                            new IpcMessage(IpcMessageType.Input, payload),
                            operationToken))
                    {
                        return false;
                    }

                    using var ackCts = CancellationTokenSource.CreateLinkedTokenSource(
                        operationToken);
                    ackCts.CancelAfter(OperationAckTimeout);
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

            return generation == Volatile.Read(ref _inputGeneration);
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

    public async Task<bool> TryInterruptAsync(
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _inputFrozen) != 0)
        {
            return false;
        }

        lock (_inputQueueLock)
        {
            if (_queuedInputOperations + _queuedInterrupts
                >= MaxQueuedInputOperations)
            {
                return false;
            }

            _queuedInterrupts++;
        }
        Interlocked.Increment(ref _inputGeneration);

        var requestId = NextRequestId();
        if (!_pendingInputs.TryRegister(requestId, out var completion))
        {
            ReleaseInterruptReservation();
            return false;
        }

        try
        {
            using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(
                GetSessionToken(),
                cancellationToken);
            var operationToken = operationCts.Token;
            if (Volatile.Read(ref _inputFrozen) != 0)
            {
                return false;
            }
            if (!await SendControlAsync(
                    new IpcMessage(
                        IpcMessageType.Interrupt,
                        IpcProtocol.EncodeRequestId(requestId)),
                    operationToken))
            {
                return false;
            }

            using var ackCts = CancellationTokenSource.CreateLinkedTokenSource(
                operationToken);
            ackCts.CancelAfter(OperationAckTimeout);
            return await completion.WaitAsync(ackCts.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _pendingInputs.Remove(requestId);
            ReleaseInterruptReservation();
        }
    }

    private void ReleaseInterruptReservation()
    {
        lock (_inputQueueLock)
        {
            _queuedInterrupts--;
            if (_queuedInterrupts == 0)
            {
                _interruptDrainCompletion?.TrySetResult();
                _interruptDrainCompletion = null;
            }
        }
    }

    public async Task<TerminalCloseCheckLease> BeginCloseCheckAsync(
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var stage = "not-started";
        if (_disposed)
        {
            var disposedAssessment = new TerminalCloseAssessment(
                TerminalCloseStatus.Idle);
            LogCloseCheck(disposedAssessment, stopwatch, "already-disposed", 0);
            return new TerminalCloseCheckLease(disposedAssessment);
        }

        var closeGateEntered = false;
        var inputGateEntered = false;
        long requestId = 0;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                GetSessionToken());
            timeoutCts.CancelAfter(CloseCheckTimeout);
            var closeCheckToken = timeoutCts.Token;

            stage = "waiting-close-gate";
            await _closeCheckGate.WaitAsync(closeCheckToken);
            closeGateEntered = true;
            // Invalidate operations that reserved capacity but have not entered
            // the input gate. Only writes already acknowledged by Host may
            // precede the close-check frame on the ordered control pipe.
            Interlocked.Exchange(ref _inputFrozen, 1);
            Interlocked.Increment(ref _inputGeneration);
            stage = "draining-ordinary-input";
            await _inputGate.WaitAsync(closeCheckToken);
            inputGateEntered = true;
            stage = "draining-interrupt-input";
            await GetInterruptDrainTask().WaitAsync(closeCheckToken);

            TerminalCloseAssessment assessment;
            if (!IsRunning)
            {
                stage = "session-stopped";
                assessment = new TerminalCloseAssessment(
                    IsFailed
                        ? TerminalCloseStatus.SessionUnavailable
                        : TerminalCloseStatus.Idle);
            }
            else
            {
                stage = "registering-request";
                requestId = NextRequestId();
                var source = new TaskCompletionSource<TerminalCloseAssessment>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_closeCheckLock)
                {
                    _pendingCloseCheckRequestId = requestId;
                    _pendingCloseCheck = source;
                }

                try
                {
                    stage = "sending-request";
                    if (!await SendControlAsync(
                            new IpcMessage(
                                IpcMessageType.CloseCheck,
                                IpcProtocol.EncodeRequestId(requestId)),
                            closeCheckToken))
                    {
                        stage = "request-send-failed";
                        assessment = new TerminalCloseAssessment(
                            TerminalCloseStatus.SessionUnavailable);
                    }
                    else
                    {
                        stage = "waiting-host-result";
                        assessment = await source.Task.WaitAsync(closeCheckToken);
                        stage = "host-result-received";
                    }
                }
                catch (OperationCanceledException)
                {
                    if (!IsRunning && !IsFailed)
                    {
                        stage = "session-exited-during-check";
                        assessment = new TerminalCloseAssessment(
                            TerminalCloseStatus.Idle);
                    }
                    else if (!cancellationToken.IsCancellationRequested)
                    {
                        stage += "-timed-out";
                        assessment = new TerminalCloseAssessment(
                            TerminalCloseStatus.CloseCheckTimedOut);
                    }
                    else
                    {
                        stage += "-canceled";
                        assessment = new TerminalCloseAssessment(
                            TerminalCloseStatus.SessionUnavailable);
                    }
                }
                finally
                {
                    lock (_closeCheckLock)
                    {
                        if (_pendingCloseCheckRequestId == requestId)
                        {
                            _pendingCloseCheckRequestId = 0;
                            _pendingCloseCheck = null;
                        }
                    }
                }
            }

            LogCloseCheck(assessment, stopwatch, stage, requestId);
            return new TerminalCloseCheckLease(
                assessment,
                resume => EndCloseCheckAsync(
                    requestId,
                    resume,
                    inputGateEntered,
                    closeGateEntered));
        }
        catch (Exception exception)
        {
            if (closeGateEntered)
            {
                Interlocked.Exchange(ref _inputFrozen, 0);
            }
            if (inputGateEntered)
            {
                ReleaseGate(_inputGate);
            }
            if (closeGateEntered)
            {
                ReleaseGate(_closeCheckGate);
            }
            TerminalCloseAssessment assessment;
            if (exception is OperationCanceledException
                && !cancellationToken.IsCancellationRequested
                && IsRunning)
            {
                stage += "-timed-out";
                assessment = new TerminalCloseAssessment(
                    TerminalCloseStatus.CloseCheckTimedOut);
            }
            else
            {
                stage += "-failed";
                assessment = new TerminalCloseAssessment(
                    TerminalCloseStatus.SessionUnavailable);
            }
            if (exception is not OperationCanceledException
                and not ObjectDisposedException)
            {
                _logger.Error(exception, "Terminal close check failed.");
            }
            LogCloseCheck(assessment, stopwatch, stage, requestId);
            return new TerminalCloseCheckLease(assessment);
        }
    }

    private void LogCloseCheck(
        TerminalCloseAssessment assessment,
        Stopwatch stopwatch,
        string stage,
        long requestId)
    {
        var title = _request.Title
            .Replace('\r', ' ')
            .Replace('\n', ' ');
        if (title.Length > 128)
        {
            title = title[..128];
        }
        _logger.Info(
            $"Close check for '{title}' ({_request.Profile.Id}): "
            + $"status={assessment.Status}, detail={assessment.Detail}, "
            + $"elapsedMs={stopwatch.ElapsedMilliseconds}, stage={stage}, "
            + $"requestId={requestId}, running={IsRunning}, failed={IsFailed}.");
    }

    private async ValueTask EndCloseCheckAsync(
        long requestId,
        bool resume,
        bool inputGateEntered,
        bool closeGateEntered)
    {
        var resumeDelivered = requestId == 0 || !IsRunning;
        try
        {
            if (resume && requestId != 0 && IsRunning)
            {
                using var timeoutCts = new CancellationTokenSource(
                    CloseCheckTimeout);
                resumeDelivered = await SendControlAsync(
                    new IpcMessage(
                        IpcMessageType.CloseCheckEnd,
                        IpcProtocol.EncodeRequestId(requestId)),
                    timeoutCts.Token);
            }
        }
        finally
        {
            if (resume && resumeDelivered)
            {
                Interlocked.Exchange(ref _inputFrozen, 0);
            }
            else if (resume && IsRunning)
            {
                // Do not let App accept input that Host may still reject as part
                // of the close transaction. A broken control pipe will fail the
                // session independently; until then, staying frozen is safer.
                _logger.Error(
                    new IOException("Close-check resume was not delivered."),
                    "Terminal input remains frozen after a close-check failure.");
            }
            if (inputGateEntered)
            {
                ReleaseGate(_inputGate);
            }
            if (closeGateEntered)
            {
                ReleaseGate(_closeCheckGate);
            }
        }
    }

    private static void ReleaseGate(SemaphoreSlim gate)
    {
        try
        {
            gate.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private Task GetInterruptDrainTask()
    {
        lock (_inputQueueLock)
        {
            if (_queuedInterrupts == 0)
            {
                return Task.CompletedTask;
            }

            _interruptDrainCompletion ??= new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return _interruptDrainCompletion.Task;
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

    public AuthorityAvailability AuthorityAvailability =>
        IsAuthorityDegraded || Volatile.Read(ref _authorityCreated) == 0
            ? AuthorityAvailability.Failed
            : _authority.GetSessionAvailability(_authoritySessionId);

    public long LatestSequence
    {
        get
        {
            lock (_commitLock)
            {
                return IsAuthorityDegraded
                    ? _degradedSequence
                    : _stateHub.LatestSequence;
            }
        }
    }

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

        _ = QueueSizeUpdate(update);
        return clientId;
    }

    public Task RequestResizeAsync(
        Guid clientId,
        int columns,
        int rows,
        CancellationToken cancellationToken = default)
    {
        TerminalSizeUpdate update;
        lock (_sizeLock)
        {
            update = _sizeCoordinator.RequestResize(clientId, columns, rows);
            _latestSizeRevision = update.Revision;
        }

        var applyTask = QueueSizeUpdate(update);
        return cancellationToken.CanBeCanceled
            ? applyTask.WaitAsync(cancellationToken)
            : applyTask;
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

        return QueueSizeUpdate(update);
    }

    public Task UnregisterTerminalClientAsync(Guid clientId)
    {
        TerminalSizeUpdate update;
        lock (_sizeLock)
        {
            update = _sizeCoordinator.Unregister(clientId);
            _latestSizeRevision = update.Revision;
        }

        return QueueSizeUpdate(update);
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
        return _stateHub.TrySubscribe(out subscription)
            && subscription is not null;
    }

    public bool TryGetRecoveryState(out TerminalInitialState? state)
    {
        lock (_sizeLock)
        {
            if (!IsAuthorityDegraded
                && _stateHub.TryGetRecoveryState(out state)
                && state is not null)
            {
                return true;
            }

            if (IsAuthorityDegraded)
            {
                // Without an authority there is no provable history. Restart the
                // replica from a blank screen at the latest delivered sequence so
                // it keeps following live output.
                lock (_commitLock)
                {
                    state = new TerminalInitialState(
                        new TerminalSnapshot(
                            _degradedSequence,
                            DegradedRecoveryNotice,
                            _sizeCoordinator.CurrentSize),
                        [],
                        _degradedSequence);
                }
                return true;
            }

            if (_stateHub.LatestSequence == 0)
            {
                state = new TerminalInitialState(
                    new TerminalSnapshot(
                        0,
                        [],
                        _sizeCoordinator.CurrentSize),
                    [],
                    0);
                return true;
            }

            state = null;
            return false;
        }
    }

    private Task QueueSizeUpdate(TerminalSizeUpdate update)
    {
        lock (_sizeApplyLock)
        {
            // Callers release _sizeLock before enqueueing, so an older revision
            // can arrive after a newer one from another client.
            if (_pendingSizeUpdate is not { } pending
                || update.Revision >= pending.Revision)
            {
                _pendingSizeUpdate = update;
            }
            if (_sizeApplyTask is null || _sizeApplyTask.IsCompleted)
            {
                _sizeApplyTask = DrainSizeUpdatesAsync();
            }

            return _sizeApplyTask;
        }
    }

    private async Task DrainSizeUpdatesAsync()
    {
        await Task.Yield();
        while (true)
        {
            TerminalSizeUpdate update;
            lock (_sizeApplyLock)
            {
                if (_pendingSizeUpdate is not { } pending)
                {
                    _sizeApplyTask = null;
                    return;
                }

                update = pending;
                _pendingSizeUpdate = null;
            }

            await ApplySizeUpdateCoreAsync(update);
        }
    }

    private async Task ApplySizeUpdateCoreAsync(TerminalSizeUpdate update)
    {
        try
        {
            await _resizeGate.WaitAsync(GetSessionToken());
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (update.Revision != Volatile.Read(ref _latestSizeRevision))
            {
                return;
            }

            // Coordinator flags compare adjacent requests, not what ConPTY has
            // applied. Coalescing a duplicate or non-owner request must retain
            // the difference from the last successfully applied state.
            update = update with
            {
                SizeChanged = update.Size != _appliedSize,
                OwnerChanged = update.OwnerId != _appliedSizeOwner
            };
            if (!update.ShouldBroadcast)
            {
                return;
            }

            if (update.SizeChanged && IsRunning)
            {
                if (!await SendResizeAsync(update.Size, GetSessionToken()))
                {
                    return;
                }
            }
            else if (update.SizeChanged && _stateHub.LatestSequence == 0
                && Volatile.Read(ref _authorityCreated) == 0)
            {
                _stateHub.TryAcceptSnapshot(0, [], update.Size);
            }

            _appliedSize = update.Size;
            _appliedSizeOwner = update.OwnerId;
            SizeChanged?.Invoke(this, update);
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

    private async Task<bool> SendResizeAsync(
        TerminalSize size,
        CancellationToken cancellationToken)
    {
        var requestId = NextRequestId();
        var source = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_resizeRequestLock)
        {
            _pendingResizes.Add(requestId, source);
        }

        try
        {
            if (!await SendControlAsync(
                    new IpcMessage(
                        IpcMessageType.Resize,
                        IpcProtocol.EncodeResizeRequest(
                            requestId,
                            size.Columns,
                            size.Rows)),
                    cancellationToken))
            {
                return false;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeoutCts.CancelAfter(OperationAckTimeout);
            return await source.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            lock (_resizeRequestLock)
            {
                _pendingResizes.Remove(requestId);
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
            await SendControlAsync(
                IpcMessage.Empty(IpcMessageType.Kill),
                killCts.Token);
        }
        catch
        {
        }

        _disposed = true;
        _authority.SessionFailed -= OnAuthoritySessionFailed;
        _cts?.Cancel();
        KillHostProcess();
        DisposePipes();
        await WaitForPumpAsync(_outputReaderTask);
        await WaitForPumpAsync(_controlReaderTask);
        await WaitForPumpAsync(_heartbeatTask);

        await EnsureAuthorityRemovedAsync();

        _hostProcess?.Dispose();
        _stateHub.Dispose();
        _cts?.Dispose();
        _controlSendGate.Dispose();
        _inputGate.Dispose();
        _resizeGate.Dispose();
        _closeCheckGate.Dispose();
        CompleteTermination();
    }

    public void KillForShutdown()
    {
        IsRunning = false;
        _cts?.Cancel();
        KillHostProcess();
        DisposePipes();
        CompleteTermination();
    }

    private async Task CleanupAfterFailedStartAsync(bool killHost)
    {
        _cts?.Cancel();
        if (killHost)
        {
            KillHostProcess();
        }

        DisposePipes();
        _hostProcess?.Dispose();
        _hostProcess = null;
        _cts?.Dispose();
        _cts = null;
        await EnsureAuthorityRemovedAsync();
    }

    private void StartHostProcess(
        string outputPipeName,
        string controlPipeName,
        string nonce,
        int columns,
        int rows)
    {
        var args = BuildHostArguments(
            outputPipeName,
            controlPipeName,
            nonce,
            columns,
            rows);
        if (_request.RunAsAdministrator)
        {
            try
            {
                _hostProcess = Process.Start(CreateHostStartInfo(args, elevated: true))
                    ?? throw new InvalidOperationException("Unable to start terminal host.");
                _logger.Info(
                    $"Started elevated host process {_hostProcess.Id} for {_request.Profile.DisplayName}.");
                return;
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
            {
                // Declining UAC should still give the user a working terminal,
                // just without elevation, instead of an empty tab.
                ElevationDeclined = true;
                _logger.Info(
                    $"Administrator elevation for '{_request.Title}' was declined; "
                    + "starting it with normal privileges.");
            }
        }

        _hostProcess = Process.Start(CreateHostStartInfo(args, elevated: false))
            ?? throw new InvalidOperationException("Unable to start terminal host.");
        _logger.Info(
            $"Started host process {_hostProcess.Id} for {_request.Profile.DisplayName}.");
    }

    private ProcessStartInfo CreateHostStartInfo(string args, bool elevated)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _paths.HostExecutablePath,
            Arguments = args,
            WorkingDirectory = _paths.BaseDirectory,
            UseShellExecute = elevated,
            CreateNoWindow = true
        };

        if (elevated)
        {
            startInfo.Verb = "runas";
        }

        return startInfo;
    }

    private string BuildHostArguments(
        string outputPipeName,
        string controlPipeName,
        string nonce,
        int columns,
        int rows)
    {
        return string.Join(
            ' ',
            "--output-pipe",
            outputPipeName,
            "--control-pipe",
            controlPipeName,
            "--nonce",
            nonce,
            "--exe",
            Encode(_request.Profile.ExecutablePath),
            "--args",
            Encode(_request.Profile.Arguments),
            "--cwd",
            Encode(_request.Profile.WorkingDirectory),
            "--profile-id",
            Encode(_request.Profile.Id),
            "--cols",
            columns.ToString(),
            "--rows",
            rows.ToString());
    }

    private static string Encode(string value)
    {
        return value.Length == 0
            ? "."
            : Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    private async Task ReadOutputLoopAsync(CancellationToken cancellationToken)
    {
        if (_outputPipe is null)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await IpcProtocol.ReadAsync(
                    _outputPipe,
                    cancellationToken);
                if (message is null)
                {
                    if (!_outputCompleted.Task.IsCompleted)
                    {
                        throw new EndOfStreamException(
                            "Terminal output pipe closed before its completion boundary.");
                    }
                    return;
                }

                switch (message.Value.Type)
                {
                    case IpcMessageType.Output:
                        await CommitAuthorityOutputAsync(
                            message.Value.Payload,
                            cancellationToken);
                        break;

                    case IpcMessageType.ResizeBoundary:
                        await CommitAuthorityResizeAsync(
                            message.Value.Payload,
                            cancellationToken);
                        break;

                    case IpcMessageType.OutputCompleted:
                    {
                        var complete = message.Value.Payload.Length == 1
                            && message.Value.Payload[0] == 1;
                        _outputCompleted.TrySetResult(complete);
                        return;
                    }

                    default:
                        throw new InvalidDataException(
                            $"Unexpected output-pipe message: {message.Value.Type}");
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
                FailSession($"终端输出连接中断：{exception.Message}", exception);
            }
        }
    }

    private Task CommitAuthorityOutputAsync(
        byte[] data,
        CancellationToken cancellationToken)
    {
        return CommitTerminalEventAsync(data, resize: null, cancellationToken);
    }

    private async Task CommitAuthorityResizeAsync(
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var (requestId, columns, rows) = IpcProtocol.DecodeResizeRequest(payload);
        var size = TerminalSize.Clamp(columns, rows);
        await CommitTerminalEventAsync([], size, cancellationToken);
        lock (_resizeRequestLock)
        {
            if (_pendingResizes.TryGetValue(requestId, out var source))
            {
                source.TrySetResult(true);
            }
        }
    }

    private async Task CommitTerminalEventAsync(
        byte[] data,
        TerminalSize? resize,
        CancellationToken cancellationToken)
    {
        TerminalAuthorityCommit? commit = null;
        Exception? authorityFailure = null;
        if (!IsAuthorityDegraded)
        {
            // Only this output loop commits events, so the hub sequence cannot
            // advance while the authority processes this one.
            var sequence = _stateHub.LatestSequence + 1;
            try
            {
                commit = resize is { } size
                    ? await _authority.ResizeAsync(
                        _authoritySessionId,
                        sequence,
                        size,
                        cancellationToken)
                    : await _authority.ProcessOutputAsync(
                        _authoritySessionId,
                        sequence,
                        data,
                        cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                authorityFailure = exception;
            }
        }

        string? degradedReason = null;
        lock (_commitLock)
        {
            if (!IsAuthorityDegraded)
            {
                if (commit is not null
                    && TryApplyAuthorityCheckpoint(commit)
                    && _stateHub.TryCommitAuthorityEvent(
                        commit.Sequence,
                        data,
                        resize,
                        commit.SnapshotSequence == commit.Sequence
                            ? commit.Snapshot
                            : null,
                        resize ?? commit.Size,
                        out var output)
                    && output is not null)
                {
                    InvokeOutput(output);
                    return;
                }

                var reason = DescribeAuthorityFailure(authorityFailure);
                if (TryEnterDegradedModeLocked(reason))
                {
                    degradedReason = reason;
                }
            }

            // A state-sync failure must never cost the user's running task.
            // Local replicas keep receiving ordered output; it just can no longer
            // be proven to remote browsers.
            InvokeOutput(new TerminalOutput(++_degradedSequence, data, resize));
        }

        if (degradedReason is not null)
        {
            OnAuthorityDegraded(degradedReason, authorityFailure);
        }
    }

    private void DegradeAuthority(string reason, Exception? exception)
    {
        bool degraded;
        lock (_commitLock)
        {
            degraded = TryEnterDegradedModeLocked(reason);
        }

        if (degraded)
        {
            OnAuthorityDegraded(reason, exception);
        }
    }

    private bool TryEnterDegradedModeLocked(string reason)
    {
        if (IsAuthorityDegraded)
        {
            return false;
        }

        // Continue numbering after the last authority-committed event, and end
        // every remote subscription exactly at that boundary.
        _degradedSequence = _stateHub.LatestSequence;
        Volatile.Write(ref _authorityDegraded, 1);
        _stateHub.Fail(new TerminalStateUnavailableException(reason));
        return true;
    }

    private void OnAuthorityDegraded(string reason, Exception? exception)
    {
        StartAuthorityRemoval();
        if (_disposed || IsFailed || _completion.Task.IsCompleted)
        {
            return;
        }

        _logger.Error(
            exception ?? new TerminalAuthorityException(reason),
            $"Terminal state sync for '{_request.Title}' was disabled; "
            + $"the terminal keeps running without remote access: {reason}");
        AuthorityDegraded?.Invoke(this, reason);
    }

    private static string DescribeAuthorityFailure(Exception? exception)
    {
        return exception switch
        {
            null => "终端状态提交被拒绝",
            TimeoutException => "状态同步超时",
            TerminalAuthorityException authority =>
                DescribeAuthorityFailure(authority.Message),
            _ => $"状态同步异常（{exception.Message}）"
        };
    }

    private static string DescribeAuthorityFailure(string reason)
    {
        if (reason.StartsWith("stateAmplificationUnsupported", StringComparison.Ordinal))
        {
            return "输出包含状态同步不支持的控制序列";
        }

        if (reason.StartsWith("stateTooLarge", StringComparison.Ordinal))
        {
            return "终端状态超过 8 MiB 同步上限";
        }

        if (reason.StartsWith("authorityTimeout", StringComparison.Ordinal)
            || reason.StartsWith("authorityRecoveryTimeout", StringComparison.Ordinal))
        {
            return "状态同步超时";
        }

        return $"状态同步异常（{reason}）";
    }

    private bool TryApplyAuthorityCheckpoint(TerminalAuthorityCommit commit)
    {
        return commit.Snapshot is null
            || commit.SnapshotSequence == commit.Sequence
            || commit.SnapshotSequence is { } sequence
                && commit.SnapshotSize is { } size
                && _stateHub.TryAcceptSnapshot(
                    sequence,
                    commit.Snapshot,
                    size);
    }

    private void InvokeOutput(TerminalOutput output)
    {
        EventHandler<TerminalOutput>? handler;
        lock (_subscribeLock)
        {
            handler = OutputReceived;
        }

        if (handler is null)
        {
            return;
        }

        foreach (EventHandler<TerminalOutput> callback in handler.GetInvocationList())
        {
            try
            {
                callback(this, output);
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Terminal output subscriber failed.");
            }
        }
    }

    private async Task ReadControlLoopAsync(CancellationToken cancellationToken)
    {
        if (_controlPipe is null)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await IpcProtocol.ReadAsync(
                    _controlPipe,
                    cancellationToken);
                if (message is null)
                {
                    throw new EndOfStreamException(
                        "Terminal control pipe closed unexpectedly.");
                }

                Interlocked.Exchange(ref _lastResponseTicks, Environment.TickCount64);
                switch (message.Value.Type)
                {
                    case IpcMessageType.Exit:
                    {
                        var status = IpcProtocol.DecodeExitStatus(message.Value.Payload);
                        var drained = false;
                        if (status.OutputComplete)
                        {
                            try
                            {
                                drained = await _outputCompleted.Task.WaitAsync(
                                    OperationAckTimeout,
                                    cancellationToken);
                            }
                            catch
                            {
                                drained = false;
                            }
                        }

                        IsRunning = false;
                        ExitCode = status.ExitCode;
                        OutputComplete = status.OutputComplete && drained;
                        if (!OutputComplete)
                        {
                            Interlocked.Exchange(ref _failed, 1);
                            FailureReason = "终端已退出，但最终输出不完整，状态无法验证";
                            _stateHub.Fail(new InvalidDataException(FailureReason));
                            StartAuthorityRemoval();
                            Failed?.Invoke(this, FailureReason);
                        }
                        Exited?.Invoke(this, status.ExitCode);
                        CompleteTermination();
                        _cts?.Cancel();
                        return;
                    }

                    case IpcMessageType.Error:
                        throw new InvalidOperationException(
                            IpcProtocol.DecodeText(message.Value.Payload));

                    case IpcMessageType.Heartbeat:
                        break;

                    case IpcMessageType.InputAck:
                    case IpcMessageType.InterruptAck:
                    {
                        var result = IpcProtocol.DecodeInputResult(message.Value.Payload);
                        _pendingInputs.TryComplete(result.RequestId, result.Accepted);
                        break;
                    }

                    case IpcMessageType.ResizeAck:
                    {
                        var result = IpcProtocol.DecodeInputResult(message.Value.Payload);
                        lock (_resizeRequestLock)
                        {
                            if (_pendingResizes.TryGetValue(
                                    result.RequestId,
                                    out var source))
                            {
                                source.TrySetResult(result.Accepted);
                            }
                        }
                        break;
                    }

                    case IpcMessageType.CloseCheckResult:
                    {
                        var result = IpcProtocol.DecodeCloseCheckResult(
                            message.Value.Payload);
                        lock (_closeCheckLock)
                        {
                            if (_pendingCloseCheckRequestId == result.RequestId)
                            {
                                _pendingCloseCheck?.TrySetResult(
                                    result.Assessment);
                            }
                        }
                        break;
                    }

                    default:
                        throw new InvalidDataException(
                            $"Unexpected control-pipe message: {message.Value.Type}");
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
                FailSession($"终端控制连接中断：{exception.Message}", exception);
            }
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
                if (!IsRunning)
                {
                    break;
                }

                var before = Interlocked.Read(ref _lastResponseTicks);
                await SendControlAsync(
                    IpcMessage.Empty(IpcMessageType.Heartbeat),
                    cancellationToken);
                await Task.Delay(HeartbeatTimeout, cancellationToken);

                if (Interlocked.Read(ref _lastResponseTicks) > before)
                {
                    missed = 0;
                    continue;
                }

                if (++missed >= MaxMissedHeartbeats)
                {
                    FailSession("终端宿主进程无响应，已终止会话");
                    break;
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
                _logger.Error(exception, "Heartbeat loop failed.");
            }
        }
    }

    private async Task<bool> SendControlAsync(
        IpcMessage message,
        CancellationToken cancellationToken = default)
    {
        if (_disposed || _controlPipe is null || !_controlPipe.IsConnected)
        {
            return false;
        }

        using var linkedCts = CreateLinkedSessionToken(cancellationToken);
        var effectiveToken = linkedCts?.Token
            ?? (cancellationToken.CanBeCanceled
                ? cancellationToken
                : GetSessionToken());
        try
        {
            await _controlSendGate.WaitAsync(effectiveToken);
        }
        catch
        {
            return false;
        }

        try
        {
            await IpcProtocol.WriteAsync(
                _controlPipe,
                message,
                effectiveToken);
            return true;
        }
        catch (Exception exception) when (exception is OperationCanceledException
            or IOException
            or InvalidDataException
            or ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            try
            {
                _controlSendGate.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void OnAuthoritySessionFailed(
        object? sender,
        AuthoritySessionFailedEventArgs args)
    {
        if (args.SessionId == _authoritySessionId)
        {
            DegradeAuthority(
                DescribeAuthorityFailure(args.Reason),
                new TerminalAuthorityException(args.Reason));
        }
    }

    private void FailSession(string message, Exception? exception = null)
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0)
        {
            return;
        }

        IsRunning = false;
        FailureReason = message;
        var failure = exception ?? new InvalidOperationException(message);
        _stateHub.Fail(failure);
        _logger.Error(failure, message);
        Failed?.Invoke(this, message);
        _cts?.Cancel();
        KillHostProcess();
        StartAuthorityRemoval();
        CompleteTermination();
    }

    private void StartAuthorityRemoval()
    {
        lock (_authorityRemovalLock)
        {
            if (Interlocked.Exchange(ref _authorityCreated, 0) == 0)
            {
                return;
            }

            _authorityRemovalTask = RemoveAuthoritySessionCoreAsync();
        }
    }

    private async Task EnsureAuthorityRemovedAsync()
    {
        StartAuthorityRemoval();
        Task removalTask;
        lock (_authorityRemovalLock)
        {
            removalTask = _authorityRemovalTask;
        }

        await removalTask.ConfigureAwait(false);
    }

    private async Task RemoveAuthoritySessionCoreAsync()
    {
        try
        {
            await _authority.RemoveSessionAsync(_authoritySessionId)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Error(
                exception,
                $"Failed to remove authority state {_authoritySessionId:N}.");
        }
    }

    private long NextRequestId()
    {
        var requestId = Interlocked.Increment(ref _nextRequestId);
        return requestId > 0
            ? requestId
            : throw new InvalidOperationException("IPC request ID space exhausted.");
    }

    private CancellationTokenSource? CreateLinkedSessionToken(
        CancellationToken cancellationToken)
    {
        var sessionToken = GetSessionToken();
        if (sessionToken.CanBeCanceled && cancellationToken.CanBeCanceled)
        {
            return CancellationTokenSource.CreateLinkedTokenSource(
                sessionToken,
                cancellationToken);
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

    private void KillHostProcess()
    {
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
    }

    private void DisposePipes()
    {
        try
        {
            _outputPipe?.Dispose();
        }
        catch
        {
        }

        try
        {
            _controlPipe?.Dispose();
        }
        catch
        {
        }
    }

    private static async Task WaitForPumpAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.WaitAsync(ReaderStopTimeout);
        }
        catch
        {
        }
    }

    private void CompleteTermination()
    {
        if (Interlocked.Exchange(ref _terminated, 1) != 0)
        {
            return;
        }

        _pendingInputs.FailAll();
        lock (_resizeRequestLock)
        {
            foreach (var source in _pendingResizes.Values)
            {
                source.TrySetResult(false);
            }
            _pendingResizes.Clear();
        }

        lock (_closeCheckLock)
        {
            _pendingCloseCheck?.TrySetResult(
                new TerminalCloseAssessment(
                    IsFailed
                        ? TerminalCloseStatus.SessionUnavailable
                        : TerminalCloseStatus.Idle));
            _pendingCloseCheck = null;
            _pendingCloseCheckRequestId = 0;
        }

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

    private sealed class OutputSubscription : IDisposable
    {
        private TerminalSession? _session;
        private EventHandler<TerminalOutput>? _handler;

        public OutputSubscription(
            TerminalSession session,
            EventHandler<TerminalOutput> handler)
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
}
