using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using TrayTerminal.Shared;
using TrayTerminal.Shared.Ipc;
using TrayTerminal.Shared.Terminal;

namespace TrayTerminal.App.Services;

/// <summary>
/// Bridges one versioned remote connection to a recoverable terminal stream.
/// Snapshot/replay and live output share a monotonic sequence; any bounded queue
/// overflow disconnects the client instead of silently corrupting its VT state.
/// </summary>
public sealed class RemoteTerminalBridge : IAsyncDisposable
{
    public const int ProtocolVersion = 3;
    private const int OutboundQueueCapacity = 512;
    private const int MaxInputBytes = 1024 * 1024;
    private const long MaxJavaScriptSafeInteger = 9_007_199_254_740_991;
    private const int MaxClientMessageBytes = IpcProtocol.MaxPayloadLength;
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly WebSocket _webSocket;
    private readonly TerminalSession _session;
    private readonly string _terminalName;
    private readonly FileLogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _socketGate = new(1, 1);
    private readonly SemaphoreSlim _resizeSignal = new(0);
    private readonly object _operationLock = new();
    private readonly object _resizeLock = new();
    private readonly object _disconnectLock = new();
    private readonly RemoteOperationGate _clientOperationGate = new();
    private readonly HashSet<Task> _clientOperations = [];
    private readonly TaskCompletionSource<SessionTermination> _termination = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<ServerMessage> _outbound = Channel.CreateBounded<ServerMessage>(
        new BoundedChannelOptions(OutboundQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            // All producers use TryWrite. A full queue is a hard slow-client
            // boundary and aborts the bridge without retaining more payloads.
            FullMode = BoundedChannelFullMode.Wait
        });
    private TerminalStateSubscription? _stateSubscription;
    private Guid? _terminalClientId;
    private long _lastInputId;
    private RemoteResize? _pendingResize;
    private Task? _disconnectTask;
    private int _disposed;

    public RemoteTerminalBridge(
        WebSocket webSocket,
        TerminalSession session,
        string terminalName,
        FileLogger logger)
    {
        _webSocket = webSocket;
        _session = session;
        _terminalName = terminalName;
        _logger = logger;
    }

    public string TerminalName => _terminalName;

    public Task DisconnectAsync(string reason)
    {
        lock (_disconnectLock)
        {
            return _disconnectTask ??= DisconnectCoreAsync(reason);
        }
    }

    private async Task DisconnectCoreAsync(string reason)
    {
        await SendDirectAsync(
            ServerMessage.Error("renamed"),
            _cts.Token).ConfigureAwait(false);
        await CloseDirectAsync(
            WebSocketCloseStatus.PolicyViolation,
            reason).ConfigureAwait(false);
        Abort();
    }

    public async Task RunAsync()
    {
        if (_webSocket.State != WebSocketState.Open)
        {
            return;
        }

        var openResult = await OpenRemoteStreamAsync().ConfigureAwait(false);
        if (openResult.Subscription is not { } subscription)
        {
            await SendDirectAsync(
                ServerMessage.Error(openResult.FailureReason),
                CancellationToken.None).ConfigureAwait(false);
            await CloseDirectAsync(
                WebSocketCloseStatus.EndpointUnavailable,
                "Terminal state is temporarily unavailable.").ConfigureAwait(false);
            await DisposeAsync().ConfigureAwait(false);
            return;
        }

        _stateSubscription = subscription;
        var initialSize = subscription.InitialState.Snapshot.Size;
        var clientId = _session.RegisterTerminalClient(
            TerminalClientKind.Remote,
            isVisible: true,
            initialSize.Columns,
            initialSize.Rows);
        _terminalClientId = clientId;
        _session.Terminated += OnSessionTerminated;
        _session.SizeChanged += OnSizeChanged;

        if (_session.Completion.IsCompleted)
        {
            OnSessionTerminated(_session, EventArgs.Empty);
        }

        var resizeTask = PumpResizeAsync(clientId, _cts.Token);
        var sendTask = SendLoopAsync(subscription, clientId);
        try
        {
            await ReceiveLoopAsync(clientId, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException exception)
            when (exception.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
        }
        catch (Exception exception)
        {
            _logger.Info(
                $"Remote bridge for '{_terminalName}' encountered an error: "
                + exception.Message);
        }
        finally
        {
            CancelBridge();
            _outbound.Writer.TryComplete();
            try
            {
                await sendTask.ConfigureAwait(false);
            }
            catch
            {
            }

            try
            {
                await resizeTask.ConfigureAwait(false);
            }
            catch
            {
            }

            await WaitForClientOperationsAsync().ConfigureAwait(false);

            await DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<RemoteOpenResult> OpenRemoteStreamAsync()
    {
        var deadline = DateTime.UtcNow + SendTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var availability = _session.AuthorityAvailability;
            if (availability == AuthorityAvailability.Failed)
            {
                return new RemoteOpenResult(null, "stateUnavailable");
            }

            if (availability == AuthorityAvailability.Ready
                && _session.TryOpenRemoteStream(out var subscription)
                && subscription is not null)
            {
                return new RemoteOpenResult(subscription, string.Empty);
            }

            if (!_session.IsRunning)
            {
                break;
            }

            await Task.Delay(100, _cts.Token).ConfigureAwait(false);
        }

        return new RemoteOpenResult(
            null,
            _session.AuthorityAvailability == AuthorityAvailability.Failed
                ? "stateUnavailable"
                : "temporaryUnavailable");
    }

    private async Task SendLoopAsync(
        TerminalStateSubscription subscription,
        Guid clientId)
    {
        var livePumpTask = PumpLiveOutputAsync(subscription);
        try
        {
            var initial = subscription.InitialState;
            await SendAsync(
                ServerMessage.SyncStart(
                    initial.Snapshot.Sequence,
                    initial.LatestSequence,
                    initial.Snapshot.Size,
                    _session.IsSizeOwner(clientId)),
                _cts.Token).ConfigureAwait(false);
            await SendAsync(
                ServerMessage.Snapshot(
                    initial.Snapshot.Sequence,
                    initial.Snapshot.Data),
                _cts.Token).ConfigureAwait(false);

            var expected = initial.Snapshot.Sequence + 1;
            foreach (var output in initial.Replay)
            {
                if (output.Sequence != expected)
                {
                    throw new InvalidDataException(
                        "Terminal initial replay contains a sequence gap.");
                }

                await SendAsync(
                    ServerMessage.Event(output),
                    _cts.Token).ConfigureAwait(false);
                expected++;
            }

            if (expected - 1 != initial.LatestSequence)
            {
                throw new InvalidDataException(
                    "Terminal initial replay does not reach the advertised sequence.");
            }

            await SendAsync(
                ServerMessage.SyncComplete(initial.LatestSequence),
                _cts.Token).ConfigureAwait(false);
            _clientOperationGate.Open();

            // Registration can change ownership before the SizeChanged handler is
            // attached. Capture an atomic canonical state at this exact protocol
            // boundary, then suppress any older queued size notifications.
            var synchronizedSize = _session.GetCurrentSizeUpdate();
            var sentSizeRevision = synchronizedSize.Revision;
            await SendAsync(
                ServerMessage.Size(
                    synchronizedSize.Size,
                    synchronizedSize.OwnerId == clientId,
                    synchronizedSize.Revision),
                _cts.Token).ConfigureAwait(false);

            await foreach (var message in _outbound.Reader.ReadAllAsync(_cts.Token)
                               .ConfigureAwait(false))
            {
                if (message.SizeRevision is { } sizeRevision)
                {
                    if (sizeRevision <= sentSizeRevision)
                    {
                        continue;
                    }

                    sentSizeRevision = sizeRevision;
                }

                await SendAsync(message, _cts.Token).ConfigureAwait(false);
                if (message.Type is "terminalEnded" or "stateUnavailable")
                {
                    await CloseDirectAsync(
                        WebSocketCloseStatus.NormalClosure,
                        message.Type).ConfigureAwait(false);
                    CancelBridge();
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (TerminalSubscriberTooSlowException)
        {
            _logger.Info(
                $"Remote client for '{_terminalName}' cannot keep up with "
                + "terminal output; disconnecting it.");
            Abort();
        }
        catch (WebSocketException)
        {
            Abort();
        }
        catch (Exception exception)
        {
            if (_session.IsFailed)
            {
                return;
            }

            if (!_cts.IsCancellationRequested)
            {
                _logger.Info(
                    $"Remote send loop for '{_terminalName}' failed: "
                    + exception.Message);
                Abort();
            }
        }
        finally
        {
            CancelBridge();
            try
            {
                await livePumpTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private async Task PumpLiveOutputAsync(TerminalStateSubscription subscription)
    {
        var delivery = new TerminalDeliveryTracker(
            subscription.InitialState.LatestSequence);
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (_termination.Task.IsCompleted)
                {
                    var termination = await _termination.Task.ConfigureAwait(false);
                    await FinalizeTerminationAsync(
                        subscription,
                        delivery,
                        termination).ConfigureAwait(false);
                    return;
                }

                while (subscription.Outputs.TryRead(out var output))
                {
                    if (!delivery.TryObserve(output.Sequence)
                        || !TryQueue(ServerMessage.Event(output)))
                    {
                        Abort();
                        return;
                    }

                }

                var outputReady = subscription.Outputs.WaitToReadAsync(_cts.Token)
                    .AsTask();
                await Task.WhenAny(outputReady, _termination.Task)
                    .ConfigureAwait(false);
                if (outputReady.IsCompletedSuccessfully
                    && !outputReady.Result
                    && !_termination.Task.IsCompleted)
                {
                    Abort();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (TerminalSubscriberTooSlowException)
        {
            _logger.Info(
                $"Remote client for '{_terminalName}' exceeded its bounded "
                + "terminal output subscription; disconnecting it.");
            Abort();
        }
        catch (Exception exception)
        {
            if (_session.IsFailed)
            {
                try
                {
                    var termination = await _termination.Task
                        .WaitAsync(SendTimeout, _cts.Token)
                        .ConfigureAwait(false);
                    await FinalizeTerminationAsync(
                        subscription,
                        delivery,
                        termination).ConfigureAwait(false);
                }
                catch
                {
                    Abort();
                }
                return;
            }

            if (!_cts.IsCancellationRequested)
            {
                _logger.Info(
                    $"Remote output subscription for '{_terminalName}' failed: "
                    + exception.Message);
                Abort();
            }
        }
    }

    private async Task FinalizeTerminationAsync(
        TerminalStateSubscription subscription,
        TerminalDeliveryTracker delivery,
        SessionTermination termination)
    {
        while (subscription.Outputs.TryRead(out var finalOutput))
        {
            if (!delivery.TryObserve(finalOutput.Sequence)
                || finalOutput.Sequence > termination.FinalSequence
                || !TryQueue(ServerMessage.Event(finalOutput)))
            {
                QueueUnverifiableTermination(
                    termination,
                    delivery.LastContinuousSequence);
                return;
            }
        }

        // Session termination rejects/completes pending Host ACKs. Queue those
        // operation results before the terminal marker so the browser never
        // loses a known result behind terminalEnded.
        await WaitForClientOperationsAsync().ConfigureAwait(false);

        if (!delivery.Reaches(termination.FinalSequence))
        {
            QueueUnverifiableTermination(
                termination,
                delivery.LastContinuousSequence);
            return;
        }

        TryQueue(termination.Failed
            ? ServerMessage.StateUnavailable(
                termination.Reason,
                termination.FinalSequence)
            : ServerMessage.TerminalEnded(
                termination.ExitCode!.Value,
                termination.OutputComplete,
                termination.FinalSequence));
        _outbound.Writer.TryComplete();
    }

    private void QueueUnverifiableTermination(
        SessionTermination termination,
        long lastContinuousSequence)
    {
        TryQueue(ServerMessage.StateUnavailable(
            termination.Reason.Length == 0
                ? "Terminal event continuity could not be verified."
                : termination.Reason,
            lastContinuousSequence));
        _outbound.Writer.TryComplete();
    }

    private void OnSizeChanged(object? sender, TerminalSizeUpdate update)
    {
        var clientId = _terminalClientId;
        if (clientId is null)
        {
            return;
        }

        TryQueue(
            ServerMessage.Size(
                update.Size,
                update.OwnerId == clientId.Value,
                update.Revision));
    }

    private void OnSessionTerminated(object? sender, EventArgs e)
    {
        _clientOperationGate.Close();
        var finalSequence = _session.LatestSequence;
        if (_session.IsFailed)
        {
            _termination.TrySetResult(new SessionTermination(
                finalSequence,
                Failed: true,
                ExitCode: null,
                OutputComplete: false,
                _session.FailureReason ?? "Terminal authority state is unavailable."));
        }
        else if (_session.ExitCode is { } exitCode)
        {
            _termination.TrySetResult(new SessionTermination(
                finalSequence,
                Failed: false,
                exitCode,
                _session.OutputComplete,
                string.Empty));
        }
        else
        {
            Abort();
        }
    }

    private bool TryQueue(ServerMessage message)
    {
        if (Volatile.Read(ref _disposed) != 0 || _cts.IsCancellationRequested)
        {
            return false;
        }

        if (_outbound.Writer.TryWrite(message))
        {
            return true;
        }

        _logger.Info(
            $"Remote client for '{_terminalName}' exceeded its bounded "
            + "outbound queue; disconnecting it.");
        Abort();
        return false;
    }

    private async Task ReceiveLoopAsync(Guid clientId, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        while (_webSocket.State == WebSocketState.Open
            && !cancellationToken.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            using var message = new MemoryStream();
            do
            {
                result = await _webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    cancellationToken).ConfigureAwait(false);
                if (message.Length + result.Count > MaxClientMessageBytes)
                {
                    _logger.Info(
                        $"Remote client for '{_terminalName}' sent an oversized "
                        + "message; disconnecting it.");
                    Abort();
                    return;
                }

                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(
                    message.GetBuffer().AsMemory(0, (int)message.Length));
                var root = document.RootElement;
                if (!root.TryGetProperty("version", out var version)
                    || !version.TryGetInt32(out var protocolVersion)
                    || protocolVersion != ProtocolVersion
                    || !root.TryGetProperty("type", out var type))
                {
                    TryQueue(ServerMessage.Error("protocolMismatch"));
                    _outbound.Writer.TryComplete();
                    return;
                }

                switch (type.GetString())
                {
                    case "input":
                        ReceiveInput(root, isInterrupt: false);
                        break;

                    case "interrupt":
                        // Interrupt is deliberately not serialized behind an
                        // ordinary input ACK. TerminalSession cancels all queued
                        // normal input and Host prioritizes the ETX after the
                        // current 64 KiB write.
                        ReceiveInput(root, isInterrupt: true);
                        break;

                    case "resize":
                        if (_clientOperationGate.IsOpen
                            && root.TryGetProperty("cols", out var columns)
                            && columns.TryGetInt32(out var columnCount)
                            && root.TryGetProperty("rows", out var rows)
                            && rows.TryGetInt32(out var rowCount))
                        {
                            QueueResize(new RemoteResize(
                                columnCount,
                                rowCount));
                        }
                        break;
                }
            }
            catch (JsonException)
            {
                // Malformed messages are ignored without allocating a response.
            }
        }
    }

    private void ReceiveInput(JsonElement root, bool isInterrupt)
    {
        if (!root.TryGetProperty("inputId", out var inputIdProperty)
            || !inputIdProperty.TryGetInt64(out var inputId)
            || inputId <= 0
            || inputId > MaxJavaScriptSafeInteger)
        {
            return;
        }

        if (inputId <= _lastInputId)
        {
            TryQueue(isInterrupt
                ? ServerMessage.InterruptAck(inputId, accepted: false)
                : ServerMessage.InputAck(inputId, accepted: false));
            return;
        }

        _lastInputId = inputId;
        string? text = null;
        if (!isInterrupt)
        {
            if (!root.TryGetProperty("data", out var dataProperty))
            {
                TryQueue(ServerMessage.InputAck(inputId, accepted: false));
                return;
            }

            text = dataProperty.GetString();
            if (string.IsNullOrEmpty(text)
                || Encoding.UTF8.GetByteCount(text) > MaxInputBytes)
            {
                TryQueue(ServerMessage.InputAck(inputId, accepted: false));
                return;
            }
        }

        StartClientOperation(inputId, text, isInterrupt);
    }

    private void StartClientOperation(long inputId, string? text, bool isInterrupt)
    {
        Task operationTask;
        lock (_operationLock)
        {
            if (!_clientOperationGate.TryBegin(isInterrupt))
            {
                TryQueue(isInterrupt
                    ? ServerMessage.InterruptAck(inputId, accepted: false)
                    : ServerMessage.InputAck(inputId, accepted: false));
                return;
            }

            operationTask = RunClientOperationAsync(
                inputId,
                text,
                isInterrupt,
                _cts.Token);
            _clientOperations.Add(operationTask);
        }

        _ = operationTask.ContinueWith(
            completed =>
            {
                lock (_operationLock)
                {
                    _clientOperations.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RunClientOperationAsync(
        long inputId,
        string? text,
        bool isInterrupt,
        CancellationToken cancellationToken)
    {
        var accepted = false;
        try
        {
            accepted = isInterrupt
                ? await _session.TryInterruptAsync(cancellationToken)
                    .ConfigureAwait(false)
                : await _session.TrySendInputAsync(text!, cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (!_cts.IsCancellationRequested)
            {
                _logger.Info(
                    $"Remote input operation for '{_terminalName}' failed: "
                    + exception.Message);
            }
        }
        finally
        {
            _clientOperationGate.End();

            TryQueue(isInterrupt
                ? ServerMessage.InterruptAck(inputId, accepted)
                : ServerMessage.InputAck(inputId, accepted));
        }
    }

    private void QueueResize(RemoteResize resize)
    {
        var signal = false;
        lock (_resizeLock)
        {
            signal = _pendingResize is null;
            _pendingResize = resize;
        }

        if (signal)
        {
            _resizeSignal.Release();
        }
    }

    private async Task PumpResizeAsync(
        Guid clientId,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await _resizeSignal.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);

                while (true)
                {
                    RemoteResize? resize;
                    lock (_resizeLock)
                    {
                        resize = _pendingResize;
                        _pendingResize = null;
                    }

                    if (resize is null)
                    {
                        break;
                    }

                    await _session.RequestResizeAsync(
                        clientId,
                        resize.Columns,
                        resize.Rows,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task WaitForClientOperationsAsync()
    {
        Task[] operations;
        lock (_operationLock)
        {
            operations = _clientOperations.ToArray();
        }

        try
        {
            await Task.WhenAll(operations).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task SendAsync(ServerMessage message, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutCts.CancelAfter(SendTimeout);
        await _socketGate.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
            await _webSocket.SendAsync(
                json,
                WebSocketMessageType.Text,
                endOfMessage: true,
                timeoutCts.Token).ConfigureAwait(false);
        }
        finally
        {
            _socketGate.Release();
        }
    }

    private async Task SendDirectAsync(
        ServerMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            await SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task CloseDirectAsync(
        WebSocketCloseStatus status,
        string description)
    {
        try
        {
            using var closeCts = new CancellationTokenSource(CloseTimeout);
            await _socketGate.WaitAsync(closeCts.Token).ConfigureAwait(false);
            try
            {
                await _webSocket.CloseAsync(
                    status,
                    description,
                    closeCts.Token).ConfigureAwait(false);
            }
            finally
            {
                _socketGate.Release();
            }
        }
        catch
        {
        }
    }

    private void CancelBridge()
    {
        _clientOperationGate.Close();
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Abort()
    {
        CancelBridge();
        try
        {
            _webSocket.Abort();
        }
        catch
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _session.Terminated -= OnSessionTerminated;
        _session.SizeChanged -= OnSizeChanged;
        _stateSubscription?.Dispose();
        _stateSubscription = null;
        _outbound.Writer.TryComplete();
        CancelBridge();

        if (_terminalClientId is { } clientId)
        {
            _terminalClientId = null;
            try
            {
                await _session.UnregisterTerminalClientAsync(clientId)
                    .ConfigureAwait(false);
            }
            catch
            {
            }
        }

        if (_webSocket.State == WebSocketState.Open)
        {
            await CloseDirectAsync(
                WebSocketCloseStatus.NormalClosure,
                "Bridge disposed").ConfigureAwait(false);
        }

        _webSocket.Dispose();
        _resizeSignal.Dispose();
        _socketGate.Dispose();
        _cts.Dispose();
    }

    private sealed record RemoteOpenResult(
        TerminalStateSubscription? Subscription,
        string FailureReason);

    private sealed record RemoteResize(int Columns, int Rows);

    private sealed record SessionTermination(
        long FinalSequence,
        bool Failed,
        int? ExitCode,
        bool OutputComplete,
        string Reason);

    private sealed record ServerMessage
    {
        public required string Type { get; init; }

        public int Version { get; init; } = ProtocolVersion;

        public string? Sequence { get; init; }

        public string? SnapshotSequence { get; init; }

        public string? LatestSequence { get; init; }

        public byte[]? Data { get; init; }

        public int? Cols { get; init; }

        public int? Rows { get; init; }

        public bool? CanResize { get; init; }

        [JsonIgnore]
        public long? SizeRevision { get; init; }

        public long? InputId { get; init; }

        public bool? Accepted { get; init; }

        public string? Reason { get; init; }

        public static ServerMessage SyncStart(
            long snapshotSequence,
            long latestSequence,
            TerminalSize size,
            bool canResize)
        {
            return new ServerMessage
            {
                Type = "syncStart",
                SnapshotSequence = snapshotSequence.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                LatestSequence = latestSequence.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                Cols = size.Columns,
                Rows = size.Rows,
                CanResize = canResize
            };
        }

        public static ServerMessage Snapshot(long sequence, byte[] data)
        {
            return new ServerMessage
            {
                Type = "snapshot",
                Sequence = sequence.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                Data = data
            };
        }

        public static ServerMessage Event(TerminalOutput output)
        {
            return new ServerMessage
            {
                Type = output.IsResize ? "resize" : "output",
                Sequence = output.Sequence.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                Data = output.IsResize ? null : output.Data,
                Cols = output.Resize?.Columns,
                Rows = output.Resize?.Rows
            };
        }

        public static ServerMessage SyncComplete(long sequence)
        {
            return new ServerMessage
            {
                Type = "syncComplete",
                Sequence = sequence.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
            };
        }

        public static ServerMessage Size(
            TerminalSize size,
            bool canResize,
            long revision)
        {
            return new ServerMessage
            {
                Type = "size",
                Cols = size.Columns,
                Rows = size.Rows,
                CanResize = canResize,
                SizeRevision = revision
            };
        }

        public static ServerMessage InputAck(long inputId, bool accepted)
        {
            return new ServerMessage
            {
                Type = "inputAck",
                InputId = inputId,
                Accepted = accepted
            };
        }

        public static ServerMessage InterruptAck(long inputId, bool accepted)
        {
            return new ServerMessage
            {
                Type = "interruptAck",
                InputId = inputId,
                Accepted = accepted
            };
        }

        public static ServerMessage TerminalEnded(
            int exitCode,
            bool outputComplete,
            long finalSequence)
        {
            return new ServerMessage
            {
                Type = "terminalEnded",
                Sequence = finalSequence.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                ExitCode = exitCode,
                OutputComplete = outputComplete
            };
        }

        public static ServerMessage StateUnavailable(
            string reason,
            long finalSequence)
        {
            return new ServerMessage
            {
                Type = "stateUnavailable",
                Sequence = finalSequence.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                Reason = reason
            };
        }

        public static ServerMessage Error(string reason)
        {
            return new ServerMessage
            {
                Type = "error",
                Reason = reason
            };
        }

        public int? ExitCode { get; init; }

        public bool? OutputComplete { get; init; }
    }
}
