using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TrayTerminal.Shared;

namespace TrayTerminal.App.Services;

/// <summary>
/// Bridges a single remote browser WebSocket connection to one TerminalSession.
/// Each instance is independent — multiple bridges can connect to the same terminal.
/// </summary>
public sealed class RemoteTerminalBridge : IAsyncDisposable
{
    private readonly WebSocket _webSocket;
    private readonly TerminalSession _session;
    private readonly string _terminalName;
    private readonly FileLogger _logger;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private IDisposable? _outputSubscription;
    private bool _disposed;

    public RemoteTerminalBridge(WebSocket webSocket, TerminalSession session, string terminalName, FileLogger logger)
    {
        _webSocket = webSocket;
        _session = session;
        _terminalName = terminalName;
        _logger = logger;
    }

    public string TerminalName => _terminalName;

    /// <summary>
    /// Subscribes to terminal output and begins the WebSocket receive loop.
    /// Returns when the WebSocket closes or the terminal exits.
    /// </summary>
    public async Task RunAsync()
    {
        if (_webSocket.State != WebSocketState.Open)
        {
            return;
        }

        _outputSubscription = _session.SubscribeOutput(OnOutputReceived);

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
            await DisposeAsync();
        }
    }

    private void OnOutputReceived(object? sender, byte[] bytes)
    {
        if (_disposed || _cts.IsCancellationRequested)
        {
            return;
        }

        // Fire-and-forget: output dispatch must not block the terminal's read loop.
        _ = SendTextAsync(Convert.ToBase64String(bytes));
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

    private async Task SendTextAsync(string text)
    {
        if (_disposed || _webSocket.State != WebSocketState.Open)
        {
            return;
        }

        try
        {
            await _sendGate.WaitAsync(_cts.Token);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                await _webSocket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        catch (ObjectDisposedException)
        {
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Unsubscribe from terminal output first so no more fire-and-forget
        // writes are queued.
        _outputSubscription?.Dispose();
        _outputSubscription = null;

        _cts.Cancel();

        // Close the WebSocket gracefully if it's still open.
        try
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Bridge disposed",
                    CancellationToken.None);
            }
        }
        catch
        {
        }

        _webSocket.Dispose();
        _sendGate.Dispose();
        _cts.Dispose();
    }
}
