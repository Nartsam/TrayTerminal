using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using TrayTerminal.App.Dialogs;
using TrayTerminal.Shared;
using TrayTerminal.Shared.Ipc;

namespace TrayTerminal.App.Services;

public sealed class TerminalSession : IAsyncDisposable
{
    private readonly NewTerminalRequest _request;
    private readonly PortablePaths _paths;
    private readonly FileLogger _logger;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _subscribeLock = new();
    private NamedPipeServerStream? _pipe;
    private Process? _hostProcess;
    private CancellationTokenSource? _cts;
    private Task? _readerTask;
    private bool _disposed;

    public TerminalSession(NewTerminalRequest request, PortablePaths paths, FileLogger logger)
    {
        _request = request;
        _paths = paths;
        _logger = logger;
    }

    public event EventHandler<byte[]>? OutputReceived;

    public event EventHandler<int>? Exited;

    public event EventHandler<string>? Failed;

    public bool IsRunning { get; private set; }

    public bool IsAdministrator => _request.RunAsAdministrator;

    /// <summary>
    /// Thread-safe subscribe to output events. The returned token must be disposed
    /// to unsubscribe; otherwise the handler keeps the session alive.
    /// </summary>
    public IDisposable SubscribeOutput(EventHandler<byte[]> handler)
    {
        lock (_subscribeLock)
        {
            OutputReceived += handler;
        }

        return new OutputSubscription(this, handler);
    }

    private void UnsubscribeOutput(EventHandler<byte[]> handler)
    {
        lock (_subscribeLock)
        {
            OutputReceived -= handler;
        }
    }

    private sealed class OutputSubscription : IDisposable
    {
        private TerminalSession? _session;
        private EventHandler<byte[]>? _handler;

        public OutputSubscription(TerminalSession session, EventHandler<byte[]> handler)
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
            _readerTask = Task.Run(() => ReadLoopAsync(_cts.Token));
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Timed out waiting for terminal host to connect.");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            Failed?.Invoke(this, "已取消管理员授权");
            _pipe.Dispose();
            _pipe = null;
        }
    }

    public Task SendInputAsync(string text)
    {
        return SendAsync(new IpcMessage(IpcMessageType.Input, Encoding.UTF8.GetBytes(text)));
    }

    public Task ResizeAsync(int columns, int rows)
    {
        if (!IsRunning)
        {
            return Task.CompletedTask;
        }

        return SendAsync(new IpcMessage(IpcMessageType.Resize, IpcProtocol.EncodeResize(columns, rows)));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        IsRunning = false;

        try
        {
            await SendAsync(IpcMessage.Empty(IpcMessageType.Kill));
        }
        catch
        {
        }

        _disposed = true;
        _cts?.Cancel();
        if (_readerTask is not null)
        {
            try
            {
                await _readerTask;
            }
            catch
            {
            }
        }

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

        _hostProcess?.Dispose();
        _pipe?.Dispose();
        _cts?.Dispose();
        _sendGate.Dispose();
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
        }
        catch
        {
        }
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

                switch (message.Value.Type)
                {
                    case IpcMessageType.Output:
                    {
                        // Snapshot the delegate to avoid a null reference if handlers
                        // are added/removed concurrently.
                        var handler = OutputReceived;
                        handler?.Invoke(this, message.Value.Payload);
                        break;
                    }

                    case IpcMessageType.Exit:
                        IsRunning = false;
                        Exited?.Invoke(this, IpcProtocol.DecodeExit(message.Value.Payload));
                        return;

                    case IpcMessageType.Error:
                        Failed?.Invoke(this, IpcProtocol.DecodeText(message.Value.Payload));
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Terminal session read loop failed.");
            Failed?.Invoke(this, exception.Message);
        }
        finally
        {
            IsRunning = false;
        }
    }

    private async Task SendAsync(IpcMessage message)
    {
        if (_disposed || _pipe is null || !_pipe.IsConnected)
        {
            return;
        }

        try
        {
            await _sendGate.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            await IpcProtocol.WriteAsync(_pipe, message, _cts?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
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
}
