using System.Diagnostics;
using System.IO.Pipes;
using TrayTerminal.Shared;
using TrayTerminal.Shared.Ipc;
using TrayTerminal.Shared.Terminal;

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
    private readonly TerminalActivityTracker _activityTracker = new();
    private readonly TerminalPromptFilter? _promptFilter;
    private PendingResize? _pendingResize;
    private TaskCompletionSource? _inputDrainCompletion;
    private bool _acceptingResizes = true;
    private bool _acceptingInputs = true;
    private bool _inputWriteActive;
    private long? _closeCheckRequestId;

    private TerminalHostSession(
        ConPtySession conPty,
        FileLogger logger,
        string markerToken)
    {
        _conPty = conPty;
        _logger = logger;
        if (conPty.PromptTrackingEnabled)
        {
            _promptFilter = new TerminalPromptFilter(
                markerToken,
                _activityTracker.RecordPrompt);
        }
    }

    public static TerminalHostSession Start(HostOptions options, FileLogger logger)
    {
        var conPty = ConPtySession.Start(
            options.ExecutablePath,
            options.Arguments,
            options.WorkingDirectory,
            options.ProfileId,
            options.Nonce,
            options.Columns,
            options.Rows);

        logger.Info($"Started terminal process {conPty.ProcessId}: {options.ExecutablePath}");
        return new TerminalHostSession(conPty, logger, options.Nonce);
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
                    if (_promptFilter is not null)
                    {
                        var remaining = _promptFilter.Flush();
                        if (remaining.Length > 0)
                        {
                            await SendOutputAsync(
                                outputPipe,
                                remaining,
                                cancellationToken).ConfigureAwait(false);
                        }
                    }
                    return;
                }

                var output = _promptFilter is null
                    ? buffer.AsSpan(0, read).ToArray()
                    : _promptFilter.Process(buffer.AsSpan(0, read));
                if (output.Length > 0)
                {
                    await SendOutputAsync(
                        outputPipe,
                        output,
                        cancellationToken).ConfigureAwait(false);
                }
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

                case IpcMessageType.CloseCheck:
                {
                    var requestId = IpcProtocol.DecodeRequestId(
                        message.Value.Payload);
                    await HandleCloseCheckAsync(
                        controlPipe,
                        requestId,
                        cancellationToken).ConfigureAwait(false);
                    break;
                }

                case IpcMessageType.CloseCheckEnd:
                    EndCloseCheck(IpcProtocol.DecodeRequestId(
                        message.Value.Payload));
                    break;

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
            if (!_acceptingInputs
                || _ordinaryInputs.Count + _interrupts.Count >= MaxQueuedInputChunks)
            {
                return false;
            }

            _ordinaryInputs.Enqueue(input);
            _activityTracker.RecordInput(input.Data.Span);
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
            if (!_acceptingInputs)
            {
                return new InterruptEnqueueResult([], Accepted: false);
            }

            canceled = _ordinaryInputs.Select(input => input.RequestId).ToArray();
            _ordinaryInputs.Clear();
            accepted = _interrupts.Count < MaxQueuedInputChunks;
            if (accepted)
            {
                _interrupts.Enqueue(new PendingInput(
                    requestId,
                    new byte[] { 0x03 },
                    IsInterrupt: true));
                _activityTracker.RecordInput([0x03]);
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
                _inputWriteActive = input is not null;
            }

            if (input is null)
            {
                continue;
            }

            try
            {
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
            finally
            {
                lock (_inputLock)
                {
                    _inputWriteActive = false;
                    CompleteInputDrainIfReady();
                }
            }
        }
    }

    private async Task HandleCloseCheckAsync(
        PipeStream controlPipe,
        long requestId,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        Task drainTask;
        var accepted = false;
        lock (_inputLock)
        {
            if (_closeCheckRequestId is null)
            {
                accepted = true;
                _closeCheckRequestId = requestId;
                _acceptingInputs = false;
                if (!_inputWriteActive
                    && _ordinaryInputs.Count == 0
                    && _interrupts.Count == 0)
                {
                    drainTask = Task.CompletedTask;
                }
                else
                {
                    // One completion source is sufficient because Host accepts
                    // only one active close transaction per session.
                    _inputDrainCompletion = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    drainTask = _inputDrainCompletion.Task;
                }
            }
            else
            {
                drainTask = Task.CompletedTask;
            }
        }

        TerminalCloseAssessment assessment;
        TerminalShellActivitySnapshot shell = default;
        var processCountAvailable = false;
        uint activeProcesses = 0;
        if (!accepted)
        {
            assessment = new TerminalCloseAssessment(
                TerminalCloseStatus.SessionUnavailable);
        }
        else
        {
            await drainTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            assessment = AssessCloseState(
                out shell,
                out processCountAvailable,
                out activeProcesses);
        }

        _logger.Info(
            $"Close check {requestId}: status={assessment.Status}, "
            + $"detail={assessment.Detail}, elapsedMs={stopwatch.ElapsedMilliseconds}, "
            + $"accepted={accepted}, promptTracking={_conPty.PromptTrackingEnabled}, "
            + $"promptObserved={shell.PromptObserved}, "
            + $"pendingSubmissions={shell.PendingSubmissions}, "
            + $"unresolvedInput={shell.HasUnresolvedInput}, "
            + $"activeShellJobs={shell.ActiveShellJobs}, "
            + $"ignoredFocusReports={shell.IgnoredFocusReports}, "
            + $"processCountAvailable={processCountAvailable}, "
            + $"activeProcesses={activeProcesses}.");

        await SendControlAsync(
            controlPipe,
            new IpcMessage(
                IpcMessageType.CloseCheckResult,
                IpcProtocol.EncodeCloseCheckResult(requestId, assessment)),
            cancellationToken).ConfigureAwait(false);
    }

    private void EndCloseCheck(long requestId)
    {
        lock (_inputLock)
        {
            if (_closeCheckRequestId != requestId)
            {
                return;
            }

            _closeCheckRequestId = null;
            _inputDrainCompletion = null;
            _acceptingInputs = true;
        }
    }

    private TerminalCloseAssessment AssessCloseState(
        out TerminalShellActivitySnapshot shell,
        out bool processCountAvailable,
        out uint activeProcesses)
    {
        shell = _activityTracker.GetSnapshot();
        processCountAvailable = _conPty.TryGetActiveProcessCount(
            out activeProcesses);
        if (processCountAvailable && activeProcesses > 1)
        {
            return new TerminalCloseAssessment(
                TerminalCloseStatus.ActiveProcesses,
                (int)Math.Min(int.MaxValue, activeProcesses - 1));
        }

        if (shell.ActiveShellJobs > 0)
        {
            return new TerminalCloseAssessment(
                TerminalCloseStatus.ActiveShellJobs,
                shell.ActiveShellJobs);
        }

        if (shell.PendingSubmissions > 0)
        {
            return new TerminalCloseAssessment(
                TerminalCloseStatus.ActiveCommand,
                (int)Math.Min(int.MaxValue, shell.PendingSubmissions));
        }

        if (shell.HasUnresolvedInput)
        {
            return new TerminalCloseAssessment(
                TerminalCloseStatus.UnknownInput);
        }

        if (!_conPty.PromptTrackingEnabled || !shell.PromptObserved)
        {
            return new TerminalCloseAssessment(
                TerminalCloseStatus.PromptUnavailable);
        }

        if (!processCountAvailable || activeProcesses != 1)
        {
            return new TerminalCloseAssessment(
                TerminalCloseStatus.ProcessTrackingUnavailable);
        }

        return new TerminalCloseAssessment(TerminalCloseStatus.Idle);
    }

    private void CompleteInputDrainIfReady()
    {
        if (!_acceptingInputs
            && !_inputWriteActive
            && _ordinaryInputs.Count == 0
            && _interrupts.Count == 0)
        {
            _inputDrainCompletion?.TrySetResult();
        }
    }

    private static Task SendOutputAsync(
        PipeStream outputPipe,
        byte[] output,
        CancellationToken cancellationToken)
    {
        return IpcProtocol.WriteAsync(
            outputPipe,
            new IpcMessage(IpcMessageType.Output, output),
            cancellationToken);
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
