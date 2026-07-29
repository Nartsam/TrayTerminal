using System.IO.Pipes;
using TrayTerminal.Shared;
using TrayTerminal.Shared.Ipc;

namespace TrayTerminal.Host.Terminal;

internal sealed class TerminalHostSession : IDisposable
{
    private readonly ConPtySession _conPty;
    private readonly FileLogger _logger;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private TerminalHostSession(ConPtySession conPty, FileLogger logger)
    {
        _conPty = conPty;
        _logger = logger;
    }

    public static TerminalHostSession Start(HostOptions options, FileLogger logger)
    {
        var conPty = ConPtySession.Start(
            options.ExecutablePath,
            options.Arguments,
            options.WorkingDirectory,
            options.Columns,
            options.Rows);

        logger.Info($"Started terminal process {conPty.ProcessId}: {options.ExecutablePath}");
        return new TerminalHostSession(conPty, logger);
    }

    public async Task<int> RunAsync(PipeStream pipe, CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // The Host owns three concurrent flows: terminal output to App, App input to
        // terminal, and shell-exit detection. Any one ending should tear down the rest.
        var outputTask = PumpTerminalOutputAsync(pipe, linkedCts.Token);
        var inputTask = PumpClientInputAsync(pipe, linkedCts.Token);
        var exitTask = WaitForExitAsync(pipe, linkedCts.Token);

        var completed = await Task.WhenAny(outputTask, inputTask, exitTask).ConfigureAwait(false);
        if (completed == exitTask)
        {
            linkedCts.Cancel();
            var exitCode = await exitTask.ConfigureAwait(false);
            await SuppressAsync(outputTask, inputTask).ConfigureAwait(false);
            return exitCode;
        }

        linkedCts.Cancel();
        _conPty.KillProcessTree();
        await SuppressAsync(outputTask, inputTask, exitTask).ConfigureAwait(false);
        return 0;
    }

    public void Dispose()
    {
        _sendGate.Dispose();
        _conPty.Dispose();
    }

    private async Task PumpTerminalOutputAsync(PipeStream pipe, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await _conPty.Output.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            var payload = buffer.AsSpan(0, read).ToArray();
            await SendAsync(pipe, new IpcMessage(IpcMessageType.Output, payload), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PumpClientInputAsync(PipeStream pipe, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await IpcProtocol.ReadAsync(pipe, cancellationToken).ConfigureAwait(false);
            if (message is null)
            {
                return;
            }

            switch (message.Value.Type)
            {
                case IpcMessageType.Input:
                    var (requestId, input) = IpcProtocol.DecodeInput(message.Value.Payload);
                    await _conPty.Input.WriteAsync(input, cancellationToken).ConfigureAwait(false);
                    await _conPty.Input.FlushAsync(cancellationToken).ConfigureAwait(false);
                    // Ack only after the bytes have been accepted by the ConPTY
                    // input pipe. The App/browser must not interpret pipe delivery
                    // to this Host as successful terminal input.
                    await SendAsync(
                        pipe,
                        new IpcMessage(
                            IpcMessageType.InputAck,
                            IpcProtocol.EncodeRequestId(requestId)),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case IpcMessageType.Resize:
                    var (columns, rows) = IpcProtocol.DecodeResize(message.Value.Payload);
                    _conPty.Resize(columns, rows);
                    break;

                case IpcMessageType.Kill:
                    _conPty.KillProcessTree();
                    return;

                case IpcMessageType.Heartbeat:
                    await SendAsync(pipe, IpcMessage.Empty(IpcMessageType.Heartbeat), cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private async Task<int> WaitForExitAsync(PipeStream pipe, CancellationToken cancellationToken)
    {
        var exitCode = await _conPty.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        _logger.Info($"Terminal process {_conPty.ProcessId} exited with code {exitCode}.");

        try
        {
            await SendAsync(pipe, new IpcMessage(IpcMessageType.Exit, IpcProtocol.EncodeExit(exitCode)), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }

        return exitCode;
    }

    private async Task SendAsync(PipeStream pipe, IpcMessage message, CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await IpcProtocol.WriteAsync(pipe, message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private static async Task SuppressAsync(params Task[] tasks)
    {
        foreach (var task in tasks)
        {
            try
            {
                await task.ConfigureAwait(false);
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
            catch
            {
            }
        }
    }
}
