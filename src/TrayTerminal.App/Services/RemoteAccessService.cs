using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using TrayTerminal.App.Controls;
using TrayTerminal.Shared;

namespace TrayTerminal.App.Services;

/// <summary>
/// Small HTTP + WebSocket server that exposes named terminal tabs to LAN browsers.
/// Lifecycle is tied to the WPF main window.
/// </summary>
public sealed class RemoteAccessService : IAsyncDisposable
{
    private const int MaxHeaderBytes = 32 * 1024;
    private static readonly TimeSpan WebSocketKeepAliveInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WebSocketKeepAliveTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ShutdownWaitTimeout = TimeSpan.FromSeconds(5);
    private static readonly byte[] HeaderTerminator = "\r\n\r\n"u8.ToArray();

    private readonly Func<string, TerminalPage?> _findTerminalPage;
    private readonly FileLogger _logger;
    private readonly int _port;
    private readonly string? _token;
    private readonly HashSet<string> _allowedTabs;
    private readonly ConcurrentDictionary<RemoteTerminalBridge, byte> _bridges = new();
    private readonly ConcurrentDictionary<Task, byte> _requestTasks = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _serverCts;
    private Task? _acceptLoopTask;
    private bool _disposed;

    public RemoteAccessService(
        Func<string, TerminalPage?> findTerminalPage,
        FileLogger logger,
        int port,
        string? token,
        HashSet<string> allowedTabs)
    {
        _findTerminalPage = findTerminalPage;
        _logger = logger;
        _port = port;
        _token = token;
        _allowedTabs = allowedTabs;
    }

    /// <summary>
    /// Starts the remote HTTP server. Does not block. Throws if the port cannot be bound.
    /// </summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var baseDir = AppContext.BaseDirectory;
        var assetsDir = Path.Combine(baseDir, "Assets");

        _serverCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        _acceptLoopTask = AcceptLoopAsync(baseDir, assetsDir, _serverCts.Token);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(string baseDir, string assetsDir, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex) when (ct.IsCancellationRequested || _disposed)
            {
                _logger.Info($"Remote access listener stopped: {ex.Message}");
                break;
            }
            catch (Exception ex)
            {
                _logger.Info($"Remote access listener error: {ex.Message}");
                continue;
            }

            var task = ProcessClientAsync(client, baseDir, assetsDir, ct);
            _requestTasks.TryAdd(task, 0);
            _ = task.ContinueWith(
                completed => _requestTasks.TryRemove(completed, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task ProcessClientAsync(TcpClient client, string baseDir, string assetsDir, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                await using var stream = client.GetStream();

                var request = await ReadHttpRequestAsync(stream, ct).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                if (!request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Method Not Allowed", ct)
                        .ConfigureAwait(false);
                    return;
                }

                var target = SplitTarget(request.Target);
                if (target.Path.Length == 0 || target.Path == "/")
                {
                    await ServeAssetAsync(stream, assetsDir, "remote-terminal.html", ct).ConfigureAwait(false);
                    return;
                }

                var path = NormalizePath(target.Path);
                if (path is null)
                {
                    await WriteTextResponseAsync(stream, 400, "Bad Request", "Bad Request", ct).ConfigureAwait(false);
                    return;
                }

                if (IsWebSocketRequest(request))
                {
                    var terminalName = path;
                    await HandleWebSocketAsync(stream, request, target.Query, terminalName, ct).ConfigureAwait(false);
                    return;
                }

                if (Path.HasExtension(path))
                {
                    await ServeAssetAsync(stream, assetsDir, path, ct).ConfigureAwait(false);
                    return;
                }

                await ServeAssetAsync(stream, assetsDir, "remote-terminal.html", ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException ex) when (_disposed || ct.IsCancellationRequested)
            {
                _logger.Info($"Remote client closed during shutdown: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Info($"Remote client request failed: {ex.Message}");
            }
        }
    }

    private async Task HandleWebSocketAsync(
        NetworkStream stream,
        HttpRequest request,
        Dictionary<string, string> query,
        string terminalName,
        CancellationToken ct)
    {
        if (!TryValidateWebSocketRequest(request, out var key))
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", "Invalid WebSocket request.", ct)
                .ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrEmpty(_token))
        {
            query.TryGetValue("token", out var requestToken);
            if (requestToken != _token)
            {
                await WriteTextResponseAsync(stream, 403, "Forbidden", "Forbidden: invalid or missing token.", ct)
                    .ConfigureAwait(false);
                return;
            }
        }

        if (!_allowedTabs.Contains(terminalName))
        {
            await WriteTextResponseAsync(
                stream,
                403,
                "Forbidden",
                $"Forbidden: '{terminalName}' is not in the remote access allow list.",
                ct).ConfigureAwait(false);
            return;
        }

        TerminalPage? page = null;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null)
        {
            page = await dispatcher.InvokeAsync(() => _findTerminalPage(terminalName));
        }
        else
        {
            page = _findTerminalPage(terminalName);
        }

        if (page is null || !page.IsRunning)
        {
            await WriteTextResponseAsync(
                stream,
                404,
                "Not Found",
                $"Terminal '{terminalName}' not found or not running.",
                ct).ConfigureAwait(false);
            return;
        }

        await WriteWebSocketHandshakeAsync(stream, key, ct).ConfigureAwait(false);

        using var webSocket = WebSocket.CreateFromStream(
            stream,
            new WebSocketCreationOptions
            {
                IsServer = true,
                KeepAliveInterval = WebSocketKeepAliveInterval,
                KeepAliveTimeout = WebSocketKeepAliveTimeout
            });

        var bridge = new RemoteTerminalBridge(webSocket, page.Session, terminalName, _logger);
        _bridges.TryAdd(bridge, 0);

        _logger.Info($"Remote client connected to terminal '{terminalName}' (total bridges: {_bridges.Count}).");

        try
        {
            await bridge.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            _bridges.TryRemove(bridge, out _);
            await bridge.DisposeAsync().ConfigureAwait(false);
            _logger.Info($"Remote client disconnected from terminal '{terminalName}' (total bridges: {_bridges.Count}).");
        }
    }

    private static async Task<HttpRequest?> ReadHttpRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[1];

        while (bytes.Length < MaxHeaderBytes)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            bytes.WriteByte(buffer[0]);
            if (EndsWith(bytes, HeaderTerminator))
            {
                break;
            }
        }

        if (bytes.Length >= MaxHeaderBytes)
        {
            return null;
        }

        var headerText = Encoding.ASCII.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0)
        {
            return null;
        }

        var requestLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2)
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                break;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out var existing)
                ? $"{existing}, {value}"
                : value;
        }

        return new HttpRequest(requestLine[0], requestLine[1], headers);
    }

    private static bool EndsWith(MemoryStream stream, byte[] suffix)
    {
        if (stream.Length < suffix.Length)
        {
            return false;
        }

        var buffer = stream.GetBuffer();
        var start = (int)stream.Length - suffix.Length;
        for (var i = 0; i < suffix.Length; i++)
        {
            if (buffer[start + i] != suffix[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsWebSocketRequest(HttpRequest request)
    {
        return request.Headers.TryGetValue("Upgrade", out var upgrade)
            && upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase)
            && request.Headers.TryGetValue("Connection", out var connection)
            && connection.Contains("upgrade", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryValidateWebSocketRequest(HttpRequest request, out string key)
    {
        key = string.Empty;
        if (!request.Headers.TryGetValue("Sec-WebSocket-Version", out var version)
            || version != "13"
            || !request.Headers.TryGetValue("Sec-WebSocket-Key", out var keyHeader))
        {
            return false;
        }

        key = keyHeader;

        try
        {
            return Convert.FromBase64String(key).Length == 16;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static async Task WriteWebSocketHandshakeAsync(NetworkStream stream, string key, CancellationToken ct)
    {
        const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        var acceptBytes = SHA1.HashData(Encoding.ASCII.GetBytes(key.Trim() + WebSocketGuid));
        var accept = Convert.ToBase64String(acceptBytes);
        var response =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n" +
            "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct).ConfigureAwait(false);
    }

    private static async Task ServeAssetAsync(
        NetworkStream stream,
        string assetsDir,
        string relativePath,
        CancellationToken ct)
    {
        relativePath = relativePath.Replace('\\', '/').TrimStart('/');
        var fullAssetsDir = Path.GetFullPath(assetsDir);
        var fullPath = Path.GetFullPath(Path.Combine(fullAssetsDir, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var assetsPrefix = fullAssetsDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(assetsPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        {
            await WriteTextResponseAsync(stream, 404, "Not Found", "Not Found", ct).ConfigureAwait(false);
            return;
        }

        var contentType = GetContentType(fullPath);
        var fileInfo = new FileInfo(fullPath);
        await WriteResponseHeadersAsync(
            stream,
            200,
            "OK",
            contentType,
            fileInfo.Length,
            "no-cache",
            ct).ConfigureAwait(false);

        await using var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await file.CopyToAsync(stream, ct).ConfigureAwait(false);
    }

    private static async Task WriteTextResponseAsync(
        NetworkStream stream,
        int statusCode,
        string reason,
        string body,
        CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        await WriteResponseHeadersAsync(
            stream,
            statusCode,
            reason,
            "text/plain; charset=utf-8",
            bytes.Length,
            "no-cache",
            ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private static async Task WriteResponseHeadersAsync(
        NetworkStream stream,
        int statusCode,
        string reason,
        string contentType,
        long contentLength,
        string cacheControl,
        CancellationToken ct)
    {
        var headers =
            $"HTTP/1.1 {statusCode} {reason}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {contentLength}\r\n" +
            $"Cache-Control: {cacheControl}\r\n" +
            "Connection: close\r\n" +
            "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), ct).ConfigureAwait(false);
    }

    private static (string Path, Dictionary<string, string> Query) SplitTarget(string target)
    {
        var question = target.IndexOf('?');
        var path = question >= 0 ? target[..question] : target;
        var queryString = question >= 0 ? target[(question + 1)..] : string.Empty;
        var query = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in queryString.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var name = equals >= 0 ? pair[..equals] : pair;
            var value = equals >= 0 ? pair[(equals + 1)..] : string.Empty;
            query[UrlDecode(name)] = UrlDecode(value);
        }

        return (path, query);
    }

    private static string? NormalizePath(string path)
    {
        if (!path.StartsWith('/'))
        {
            return null;
        }

        var decoded = UrlDecode(path).TrimStart('/');
        if (decoded.Length == 0 || decoded.Contains('\0'))
        {
            return null;
        }

        return decoded;
    }

    private static string UrlDecode(string value)
    {
        return Uri.UnescapeDataString(value.Replace("+", "%20"));
    }

    private static string GetContentType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".ico" => "image/x-icon",
            _ => "application/octet-stream"
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try { _serverCts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }

        if (_acceptLoopTask is not null)
        {
            try { await _acceptLoopTask.WaitAsync(ShutdownWaitTimeout).ConfigureAwait(false); } catch { }
        }

        var bridges = _bridges.Keys.ToArray();
        await Task.WhenAll(bridges.Select(bridge => bridge.DisposeAsync().AsTask())).ConfigureAwait(false);
        _bridges.Clear();

        var requests = _requestTasks.Keys.ToArray();
        if (requests.Length > 0)
        {
            try { await Task.WhenAll(requests).WaitAsync(ShutdownWaitTimeout).ConfigureAwait(false); } catch { }
        }

        _serverCts?.Dispose();
        _serverCts = null;
        _listener = null;
    }

    private sealed record HttpRequest(
        string Method,
        string Target,
        Dictionary<string, string> Headers);
}
