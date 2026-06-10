using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TrayTerminal.Shared;
using TrayTerminal.Shared.Ipc;

namespace TrayTerminal.App.Services;

/// <summary>
/// Bridges a single remote browser WebSocket connection to one TerminalSession.
/// Each instance is independent — multiple bridges can connect to the same terminal.
/// Terminal output flows through a bounded queue drained by a single send loop, so a
/// slow or dead client can never make the app buffer output without limit.
/// </summary>
public sealed class RemoteTerminalBridge : IAsyncDisposable
{
    // The terminal host emits chunks of at most 8 KB, so 512 queued chunks cap the
    // per-client backlog at roughly 4 MB before the client is considered too slow.
    private const int OutputQueueCapacity = 512;
    private const int MaxClientMessageBytes = IpcProtocol.MaxPayloadLength;
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);

    private readonly WebSocket _webSocket;
    private readonly TerminalSession _session;
    private readonly string _terminalName;
    private readonly FileLogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<byte[]> _outputQueue = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(OutputQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            // Producers use TryWrite, so Wait never actually blocks; a full queue
            // surfaces as TryWrite returning false and the bridge disconnecting.
            FullMode = BoundedChannelFullMode.Wait
        });
    private IDisposable? _outputSubscription;
    private int _disposed;

    public RemoteTerminalBridge(WebSocket webSocket, TerminalSession session, string terminalName, FileLogger logger)
    {
        _webSocket = webSocket;
        _session = session;
        _terminalName = terminalName;
        _logger = logger;
    }

    public string TerminalName => _terminalName;

    /// <summary>
    /// Subscribes to terminal output and begins the WebSocket receive and send loops.
    /// Returns when the WebSocket closes, the terminal exits, or the client stalls.
    /// </summary>
    public async Task RunAsync()
    {
        if (_webSocket.State != WebSocketState.Open)
        {
            return;
        }

        _session.Terminated += OnSessionTerminated;
        _outputSubscription = _session.SubscribeOutput(OnOutputReceived);
        if (_session.Completion.IsCompleted)
        {
            Abort();
            await DisposeAsync();
            return;
        }

        var sendTask = SendLoopAsync();

        try
        {
            await ReceiveLoopAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
        }
        catch (Exception ex)
        {
            _logger.Info($"Remote bridge for '{_terminalName}' encountered an error: {ex.Message}");
        }
        finally
        {
            // Stop the send loop before closing the socket so CloseAsync does not
            // race an in-flight SendAsync on the same WebSocket.
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _outputQueue.Writer.TryComplete();
            try
            {
                await sendTask;
            }
            catch
            {
            }

            await DisposeAsync();
        }
    }

    private void OnOutputReceived(object? sender, byte[] bytes)
    {
        if (Volatile.Read(ref _disposed) != 0 || _cts.IsCancellationRequested)
        {
            return;
        }

        if (!_outputQueue.Writer.TryWrite(bytes) && !_cts.IsCancellationRequested)
        {
            // The bounded queue is full: the client has fallen ~4 MB behind the
            // terminal. Drop the connection instead of buffering without limit;
            // the browser will reconnect on its own.
            _logger.Info($"Remote client for '{_terminalName}' cannot keep up with terminal output; disconnecting it.");
            Abort();
        }
    }

    private void OnSessionTerminated(object? sender, EventArgs e)
    {
        _logger.Info($"Terminal '{_terminalName}' ended; disconnecting remote client.");
        Abort();
    }

    private async Task SendLoopAsync()
    {
        try
        {
            while (await _outputQueue.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (_outputQueue.Reader.TryRead(out var chunk))
                {
                    var bytes = Encoding.UTF8.GetBytes(Convert.ToBase64String(chunk));
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    timeoutCts.CancelAfter(SendTimeout);
                    await _webSocket.SendAsync(
                        new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        timeoutCts.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (!_cts.IsCancellationRequested)
            {
                // The per-send timeout fired: the socket still reports Open but the
                // peer is not reading (suspended tab, half-open connection). The
                // cancelled SendAsync has already aborted the socket; finish tearing
                // the bridge down so the receive loop exits too.
                _logger.Info($"Remote client for '{_terminalName}' stalled for {SendTimeout.TotalSeconds:0}s during send; disconnecting it.");
                Abort();
            }
        }
        catch (WebSocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Abort()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _webSocket.Abort();
        }
        catch
        {
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
        while (_webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            using var message = new MemoryStream();
            do
            {
                result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (message.Length + result.Count > MaxClientMessageBytes)
                {
                    _logger.Info($"Remote client for '{_terminalName}' sent a message larger than {MaxClientMessageBytes} bytes; disconnecting it.");
                    Abort();
                    return;
                }

                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            message.Position = 0;
            var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var typeProp))
                {
                    continue;
                }

                switch (typeProp.GetString())
                {
                    case "input":
                        if (root.TryGetProperty("data", out var data))
                        {
                            var text = data.GetString();
                            if (!string.IsNullOrEmpty(text))
                            {
                                await _session.SendInputAsync(text);
                            }
                        }
                        break;

                    case "resize":
                        if (root.TryGetProperty("cols", out var colsProp) &&
                            root.TryGetProperty("rows", out var rowsProp))
                        {
                            var cols = Math.Clamp(colsProp.GetInt32(), 20, 500);
                            var rows = Math.Clamp(rowsProp.GetInt32(), 5, 200);
                            await _session.ResizeAsync(cols, rows);
                        }
                        break;
                }
            }
            catch (JsonException)
            {
                // Ignore malformed messages from the browser.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Unsubscribe from terminal output first so no more chunks are queued,
        // then stop both loops.
        _session.Terminated -= OnSessionTerminated;
        _outputSubscription?.Dispose();
        _outputSubscription = null;
        _outputQueue.Writer.TryComplete();
        _cts.Cancel();

        // Close the WebSocket gracefully if it's still open, with a hard cap so
        // shutdown can never hang on an unresponsive peer.
        try
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Bridge disposed",
                    closeCts.Token);
            }
        }
        catch
        {
        }

        _webSocket.Dispose();
        _cts.Dispose();
    }
}
