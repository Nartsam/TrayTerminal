using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TrayTerminal.App.Controls;
using TrayTerminal.Shared;

namespace TrayTerminal.App.Services;

/// <summary>
/// Embedded HTTP + WebSocket server that exposes named terminal tabs to LAN browsers.
/// Lifecycle is tied to the WPF main window.
/// </summary>
public sealed class RemoteAccessService : IAsyncDisposable
{
    private readonly Func<string, TerminalPage?> _findTerminalPage;
    private readonly FileLogger _logger;
    private readonly int _port;
    private readonly string? _token;
    private readonly HashSet<string> _allowedTabs;
    private readonly ConcurrentDictionary<RemoteTerminalBridge, byte> _bridges = new();
    private WebApplication? _app;
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
    /// Starts the Kestrel server. Does not block — the caller should await the
    /// returned task. Throws if the port cannot be bound.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        var baseDir = AppContext.BaseDirectory;
        var assetsDir = Path.Combine(baseDir, "Assets");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = baseDir,
            WebRootPath = assetsDir,
            ApplicationName = "TrayTerminal.Remote"
        });

        builder.WebHost.UseUrls($"http://0.0.0.0:{_port}");

        // Suppress the ASP.NET request logging — it would write to stdout/stderr
        // and conflict with our FileLogger.
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new NullLoggerProvider());

        _app = builder.Build();
        _app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30)
        });

        // Path-rewrite + WebSocket + static-file middleware.
        // Runs BEFORE UseStaticFiles so rewritten paths resolve to static files.
        _app.Use(async (HttpContext context, Func<Task> next) =>
        {
            var path = context.Request.Path.Value?.TrimStart('/');
            if (string.IsNullOrEmpty(path))
            {
                // Root → serve the remote terminal page.
                context.Request.Path = "/remote-terminal.html";
                await next();
                return;
            }

            // Serve xterm.js and xterm.css explicitly. These are loaded by
            // remote-terminal.html via relative <script>/<link> tags.
            if (path == "xterm/xterm.js" || path == "xterm/xterm.css")
            {
                var filePath = Path.Combine(baseDir, "Assets", path);
                if (File.Exists(filePath))
                {
                    context.Response.ContentType = path.EndsWith(".js")
                        ? "application/javascript; charset=utf-8"
                        : "text/css; charset=utf-8";
                    context.Response.Headers.CacheControl = "no-cache";
                    await context.Response.SendFileAsync(filePath, context.RequestAborted);
                }
                else
                {
                    context.Response.StatusCode = 404;
                }

                return;
            }

            // If the path has a file extension, it's some other static asset —
            // pass through to UseStaticFiles without rewriting.
            if (Path.HasExtension(path))
            {
                await next();
                return;
            }

            // The path is the requested terminal name.
            var terminalName = path;

            if (context.WebSockets.IsWebSocketRequest)
            {
                await HandleWebSocketAsync(context, terminalName);
                return;
            }

            // Regular browser GET with a terminal name — serve remote-terminal.html.
            context.Request.Path = "/remote-terminal.html";
            await next();
        });

        _app.UseStaticFiles();

        await _app.StartAsync(ct);
    }

    private async Task HandleWebSocketAsync(HttpContext context, string terminalName)
    {
        // Token check — only when a token is configured.
        if (!string.IsNullOrEmpty(_token))
        {
            var requestToken = context.Request.Query["token"].FirstOrDefault() ?? string.Empty;
            if (requestToken != _token)
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsync("Forbidden: invalid or missing token.", context.RequestAborted);
                return;
            }
        }

        if (!_allowedTabs.Contains(terminalName))
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsync(
                $"Forbidden: '{terminalName}' is not in the remote access allow list.",
                context.RequestAborted);
            return;
        }

        // Find the terminal. Must be done on the UI thread because it accesses
        // WPF TabControl.Items.
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
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync(
                $"Terminal '{terminalName}' not found or not running.",
                context.RequestAborted);
            return;
        }

        WebSocket webSocket;
        try
        {
            // KeepAliveTimeout makes the server abort connections whose peer stops
            // answering pings (dead device, half-open TCP). Without it a dead client
            // would hold the bridge open forever.
            webSocket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
            {
                KeepAliveInterval = TimeSpan.FromSeconds(30),
                KeepAliveTimeout = TimeSpan.FromSeconds(20)
            });
        }
        catch
        {
            return;
        }

        var bridge = new RemoteTerminalBridge(webSocket, page.Session, terminalName, _logger);
        _bridges.TryAdd(bridge, 0);

        _logger.Info($"Remote client connected to terminal '{terminalName}' (total bridges: {_bridges.Count}).");

        try
        {
            await bridge.RunAsync();
        }
        finally
        {
            _bridges.TryRemove(bridge, out _);
            await bridge.DisposeAsync();
            _logger.Info($"Remote client disconnected from terminal '{terminalName}' (total bridges: {_bridges.Count}).");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Dispose active bridges first so they gracefully close their WebSockets.
        // Do it concurrently; each bridge has its own close timeout, and shutdown
        // should not take N * timeout when many browsers are connected.
        var bridges = _bridges.Keys.ToArray();
        await Task.WhenAll(bridges.Select(bridge => bridge.DisposeAsync().AsTask()));

        _bridges.Clear();

        if (_app is not null)
        {
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
        }
    }

    /// <summary>
    /// A no-op logger provider so ASP.NET doesn't write to stdout.
    /// </summary>
    private sealed class NullLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => NullLogger.Instance;

        public void Dispose()
        {
        }

        private sealed class NullLogger : ILogger
        {
            public static readonly NullLogger Instance = new();

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => false;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
            }
        }
    }
}
