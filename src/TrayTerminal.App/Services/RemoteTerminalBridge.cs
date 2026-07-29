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
    public const int ProtocolVersion = 2;
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

    public async Task RunAsync()
    {
        if (_webSocket.State != WebSocketState.Open)
        {
            return;
        }

        if (!_session.TryOpenRemoteStream(out var subscription)
            || subscription is null)
        {
            await SendDirectAsync(
                ServerMessage.Error("stateUnavailable"),
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
            Abort();
            await DisposeAsync();
            return;
        }

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

            await DisposeAsync().ConfigureAwait(false);
        }
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
                    ServerMessage.Output(output),
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
        try
        {
            await foreach (var output in subscription.Outputs
                               .ReadAllAsync(_cts.Token)
                               .ConfigureAwait(false))
            {
                if (!TryQueue(ServerMessage.Output(output)))
                {
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
            if (!_cts.IsCancellationRequested)
            {
                _logger.Info(
                    $"Remote output subscription for '{_terminalName}' failed: "
                    + exception.Message);
                Abort();
            }
        }
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
        _logger.Info(
            $"Terminal '{_terminalName}' ended; disconnecting remote client.");
        Abort();
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
                    Abort();
                    return;
                }

                switch (type.GetString())
                {
                    case "input":
                        await ProcessInputAsync(root).ConfigureAwait(false);
                        break;

                    case "resize":
                        if (root.TryGetProperty("cols", out var columns)
                            && columns.TryGetInt32(out var columnCount)
                            && root.TryGetProperty("rows", out var rows)
                            && rows.TryGetInt32(out var rowCount))
                        {
                            await _session.RequestResizeAsync(
                                clientId,
                                columnCount,
                                rowCount).ConfigureAwait(false);
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

    private async Task ProcessInputAsync(JsonElement root)
    {
        if (!root.TryGetProperty("inputId", out var inputIdProperty)
            || !inputIdProperty.TryGetInt64(out var inputId)
            || inputId <= _lastInputId
            || inputId > MaxJavaScriptSafeInteger
            || !root.TryGetProperty("data", out var dataProperty))
        {
            return;
        }

        var text = dataProperty.GetString();
        if (string.IsNullOrEmpty(text)
            || Encoding.UTF8.GetByteCount(text) > MaxInputBytes)
        {
            TryQueue(ServerMessage.InputAck(inputId, accepted: false));
            return;
        }

        _lastInputId = inputId;
        var accepted = await _session.TrySendInputAsync(text).ConfigureAwait(false);
        TryQueue(ServerMessage.InputAck(inputId, accepted));
    }

    private async Task SendAsync(ServerMessage message, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutCts.CancelAfter(SendTimeout);
        var json = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        await _webSocket.SendAsync(
            json,
            WebSocketMessageType.Text,
            endOfMessage: true,
            timeoutCts.Token).ConfigureAwait(false);
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
            await _webSocket.CloseAsync(
                status,
                description,
                closeCts.Token).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private void CancelBridge()
    {
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
        _cts.Dispose();
    }

    private sealed record ServerMessage
    {
        public required string Type { get; init; }

        public int Version { get; init; } = ProtocolVersion;

        public long? Sequence { get; init; }

        public long? SnapshotSequence { get; init; }

        public long? LatestSequence { get; init; }

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
                SnapshotSequence = snapshotSequence,
                LatestSequence = latestSequence,
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
                Sequence = sequence,
                Data = data
            };
        }

        public static ServerMessage Output(TerminalOutput output)
        {
            return new ServerMessage
            {
                Type = "output",
                Sequence = output.Sequence,
                Data = output.Data
            };
        }

        public static ServerMessage SyncComplete(long sequence)
        {
            return new ServerMessage
            {
                Type = "syncComplete",
                Sequence = sequence
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

        public static ServerMessage Error(string reason)
        {
            return new ServerMessage
            {
                Type = "error",
                Reason = reason
            };
        }
    }
}
