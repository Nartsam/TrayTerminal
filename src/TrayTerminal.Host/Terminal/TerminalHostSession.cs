using System.IO.Pipes;
using TrayTerminal.Shared;
using TrayTerminal.Shared.Ipc;

namespace TrayTerminal.Host.Terminal;

internal sealed class TerminalHostSession : IDisposable
{
    private const int MaxQueuedInputChunks = 64;
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(3);
    private readonly ConPtySession _conPty;
    private readonly FileLogger _logger;
    private readonly SemaphoreSlim _controlSendGate = new(1, 1);
    private readonly SemaphoreSlim _inputSignal = new(0);
    private readonly SemaphoreSlim _resizeSignal = new(0);
    private readonly object _inputLock = new();
    private readonly object _resizeLock = new();
    private readonly Queue<PendingInput> _ordinaryInputs = new();
    private readonly Queue<PendingInput> _interrupts = new();
    private PendingResize? _pendingResize;
    private bool _acceptingResizes = true;

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

    public async Task<int> RunAsync(
        PipeStream outputPipe,
        PipeStream controlPipe,
        CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var outputCts = CancellationTokenSource.CreateLinkedTokenSource(
            linkedCts.Token);
        var outputTask = PumpTerminalEventsAsync(outputPipe, outputCts.Token);
        var controlTask = PumpControlAsync(controlPipe, linkedCts.Token);
        var inputTask = PumpConPtyInputAsync(controlPipe, linkedCts.Token);
        var exitTask = _conPty.WaitForExitAsync(linkedCts.Token);

        var completed = await Task.WhenAny(
            exitTask,
            outputTask,
            controlTask,
            inputTask).ConfigureAwait(false);
        if (completed != exitTask && !exitTask.IsCompleted)
        {
            linkedCts.Cancel();
            _conPty.KillProcessTree();
            await SuppressAsync(
                outputTask,
                controlTask,
                inputTask,
                exitTask).ConfigureAwait(false);
            return 1;
        }

        var exitCode = await exitTask.ConfigureAwait(false);
        _logger.Info($"Terminal process {_conPty.ProcessId} exited with code {exitCode}; draining ConPTY output.");
        var exitDeadline = DateTime.UtcNow + OutputDrainTimeout;
        var abandonedResize = StopAcceptingResizes();
        if (abandonedResize is not null)
        {
            await TrySendResizeResultBeforeDeadlineAsync(
                controlPipe,
                abandonedResize.RequestId,
                accepted: false,
                exitDeadline).ConfigureAwait(false);
        }
        _conPty.CompleteProcessExit();

        var outputComplete = false;
        try
        {
            var drainBudget = Remaining(exitDeadline) - TimeSpan.FromMilliseconds(250);
            if (drainBudget <= TimeSpan.Zero)
            {
                throw new TimeoutException();
            }

            await outputTask.WaitAsync(drainBudget).ConfigureAwait(false);
            outputComplete = true;
        }
        catch (TimeoutException)
        {
            _logger.Warn(
                $"ConPTY output for process {_conPty.ProcessId} did not drain within 3 seconds.");
        }
        catch (Exception exception)
        {
            _logger.Warn(
                $"ConPTY output for process {_conPty.ProcessId} was incomplete: {exception.Message}");
        }

        if (!outputComplete)
        {
            outputCts.Cancel();
        }

        if (outputComplete)
        {
            try
            {
                using var boundaryCts = CreateDeadlineToken(exitDeadline);
                await IpcProtocol.WriteAsync(
                    outputPipe,
                    new IpcMessage(IpcMessageType.OutputCompleted, [1]),
                    boundaryCts.Token).ConfigureAwait(false);
            }
            catch
            {
                outputComplete = false;
            }
        }

        try
        {
            using var exitCts = CreateDeadlineToken(exitDeadline);
            await SendControlAsync(
                controlPipe,
                new IpcMessage(
                    IpcMessageType.Exit,
                    IpcProtocol.EncodeExitStatus(exitCode, outputComplete)),
                exitCts.Token).ConfigureAwait(false);
        }
        catch
        {
        }

        linkedCts.Cancel();
        await SuppressAsync(
            outputTask,
            controlTask,
            inputTask).ConfigureAwait(false);
        return exitCode;
    }

    public void Dispose()
    {
        _controlSendGate.Dispose();
        _inputSignal.Dispose();
        _resizeSignal.Dispose();
        _conPty.Dispose();
    }

    private async Task PumpTerminalEventsAsync(
        PipeStream outputPipe,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        Task<int>? readTask = null;
        Task? resizeWaitTask = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            readTask ??= _conPty.Output.ReadAsync(
                buffer.AsMemory(0, buffer.Length),
                cancellationToken).AsTask();
            resizeWaitTask ??= _resizeSignal.WaitAsync(cancellationToken);
            if (!readTask.IsCompleted && !resizeWaitTask.IsCompleted)
            {
                await Task.WhenAny(readTask, resizeWaitTask).ConfigureAwait(false);
            }

            // A completed read always wins over resize. This is the crucial
            // ordering point: bytes already removed from ConPTY cannot appear
            // after a resize boundary that was requested later.
            if (readTask.IsCompleted)
            {
                var read = await readTask.ConfigureAwait(false);
                readTask = null;
                if (read == 0)
                {
                    return;
                }

                await IpcProtocol.WriteAsync(
                    outputPipe,
                    new IpcMessage(
                        IpcMessageType.Output,
                        buffer.AsSpan(0, read).ToArray()),
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            await resizeWaitTask.ConfigureAwait(false);
            resizeWaitTask = null;
            PendingResize? resize;
            lock (_resizeLock)
            {
                resize = _pendingResize;
                _pendingResize = null;
            }

            if (resize is null)
            {
                continue;
            }

            _conPty.Resize(resize.Columns, resize.Rows);
            await IpcProtocol.WriteAsync(
                outputPipe,
                new IpcMessage(
                    IpcMessageType.ResizeBoundary,
                    IpcProtocol.EncodeResizeRequest(
                        resize.RequestId,
                        resize.Columns,
                        resize.Rows)),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PumpControlAsync(
        PipeStream controlPipe,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await IpcProtocol.ReadAsync(
                controlPipe,
                cancellationToken).ConfigureAwait(false);
            if (message is null)
            {
                return;
            }

            switch (message.Value.Type)
            {
                case IpcMessageType.Input:
                {
                    var (requestId, input) = IpcProtocol.DecodeInput(message.Value.Payload);
                    if (!TryEnqueueInput(new PendingInput(
                            requestId,
                            input.ToArray(),
                            IsInterrupt: false)))
                    {
                        await SendInputResultAsync(
                            controlPipe,
                            IpcMessageType.InputAck,
                            requestId,
                            accepted: false,
                            cancellationToken).ConfigureAwait(false);
                    }
                    break;
                }

                case IpcMessageType.Interrupt:
                {
                    var requestId = IpcProtocol.DecodeRequestId(message.Value.Payload);
                    var result = EnqueueInterrupt(requestId);
                    foreach (var canceledRequestId in result.CanceledRequestIds)
                    {
                        await SendInputResultAsync(
                            controlPipe,
                            IpcMessageType.InputAck,
                            canceledRequestId,
                            accepted: false,
                            cancellationToken).ConfigureAwait(false);
                    }
                    if (!result.Accepted)
                    {
                        await SendInputResultAsync(
                            controlPipe,
                            IpcMessageType.InterruptAck,
                            requestId,
                            accepted: false,
                            cancellationToken).ConfigureAwait(false);
                    }
                    break;
                }

                case IpcMessageType.Resize:
                {
                    var (requestId, columns, rows) =
                        IpcProtocol.DecodeResizeRequest(message.Value.Payload);
                    var replaced = QueueResize(
                        new PendingResize(requestId, columns, rows));
                    if (replaced is not null)
                    {
                        await SendInputResultAsync(
                            controlPipe,
                            IpcMessageType.ResizeAck,
                            replaced.RequestId,
                            accepted: false,
                            cancellationToken).ConfigureAwait(false);
                    }
                    break;
                }

                case IpcMessageType.Kill:
                    _conPty.KillProcessTree();
                    return;

                case IpcMessageType.Heartbeat:
                    await SendControlAsync(
                        controlPipe,
                        IpcMessage.Empty(IpcMessageType.Heartbeat),
                        cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private bool TryEnqueueInput(PendingInput input)
    {
        lock (_inputLock)
        {
            if (_ordinaryInputs.Count + _interrupts.Count >= MaxQueuedInputChunks)
            {
                return false;
            }

            _ordinaryInputs.Enqueue(input);
        }

        _inputSignal.Release();
        return true;
    }

    private PendingResize? QueueResize(PendingResize resize)
    {
        var signal = false;
        PendingResize? replaced;
        lock (_resizeLock)
        {
            if (!_acceptingResizes)
            {
                return resize;
            }

            // App-side ownership coalescing normally leaves only one request.
            // Keeping only the newest here is the final hard bound if a malformed
            // or future client violates that contract.
            signal = _pendingResize is null;
            replaced = _pendingResize;
            _pendingResize = resize;
        }

        if (signal)
        {
            _resizeSignal.Release();
        }

        return replaced;
    }

    private PendingResize? StopAcceptingResizes()
    {
        lock (_resizeLock)
        {
            _acceptingResizes = false;
            var abandoned = _pendingResize;
            _pendingResize = null;
            return abandoned;
        }
    }

    private InterruptEnqueueResult EnqueueInterrupt(long requestId)
    {
        long[] canceled;
        bool accepted;
        lock (_inputLock)
        {
            canceled = _ordinaryInputs.Select(input => input.RequestId).ToArray();
            _ordinaryInputs.Clear();
            accepted = _interrupts.Count < MaxQueuedInputChunks;
            if (accepted)
            {
                _interrupts.Enqueue(new PendingInput(
                    requestId,
                    new byte[] { 0x03 },
                    IsInterrupt: true));
            }
        }

        if (accepted)
        {
            _inputSignal.Release();
        }
        return new InterruptEnqueueResult(canceled, accepted);
    }

    private async Task PumpConPtyInputAsync(
        PipeStream controlPipe,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _inputSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
            PendingInput? input;
            lock (_inputLock)
            {
                if (_interrupts.Count > 0)
                {
                    input = _interrupts.Dequeue();
                }
                else
                {
                    input = _ordinaryInputs.Count > 0
                        ? _ordinaryInputs.Dequeue()
                        : null;
                }
            }

            if (input is null)
            {
                continue;
            }

            await _conPty.Input.WriteAsync(
                input.Data,
                cancellationToken).ConfigureAwait(false);
            await _conPty.Input.FlushAsync(cancellationToken).ConfigureAwait(false);
            await SendInputResultAsync(
                controlPipe,
                input.IsInterrupt
                    ? IpcMessageType.InterruptAck
                    : IpcMessageType.InputAck,
                input.RequestId,
                accepted: true,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private Task SendInputResultAsync(
        PipeStream controlPipe,
        IpcMessageType type,
        long requestId,
        bool accepted,
        CancellationToken cancellationToken)
    {
        return SendControlAsync(
            controlPipe,
            new IpcMessage(
                type,
                IpcProtocol.EncodeInputResult(requestId, accepted)),
            cancellationToken);
    }

    private async Task SendControlAsync(
        PipeStream controlPipe,
        IpcMessage message,
        CancellationToken cancellationToken)
    {
        await _controlSendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await IpcProtocol.WriteAsync(
                controlPipe,
                message,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _controlSendGate.Release();
        }
    }

    private async Task TrySendResizeResultBeforeDeadlineAsync(
        PipeStream controlPipe,
        long requestId,
        bool accepted,
        DateTime deadline)
    {
        try
        {
            using var deadlineCts = CreateDeadlineToken(deadline);
            await SendInputResultAsync(
                controlPipe,
                IpcMessageType.ResizeAck,
                requestId,
                accepted,
                deadlineCts.Token).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static TimeSpan Remaining(DateTime deadline)
    {
        var remaining = deadline - DateTime.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static CancellationTokenSource CreateDeadlineToken(DateTime deadline)
    {
        return new CancellationTokenSource(Remaining(deadline));
    }

    private static async Task SuppressAsync(params Task[] tasks)
    {
        foreach (var task in tasks)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private sealed record PendingInput(
        long RequestId,
        ReadOnlyMemory<byte> Data,
        bool IsInterrupt);

    private sealed record InterruptEnqueueResult(
        long[] CanceledRequestIds,
        bool Accepted);

    private sealed record PendingResize(
        long RequestId,
        int Columns,
        int Rows);
}
