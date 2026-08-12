using System.IO.Pipes;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TrayTerminal.Shared;
using TrayTerminal.Shared.Ipc;
using TrayTerminal.Shared.Terminal;

namespace TrayTerminal.SmokeTests;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 1
            && args[0] is "--stdin-probe" or "--backpressure-probe")
        {
            Console.TreatControlCAsInput = true;
            Console.WriteLine("READY");
            if (args[0] == "--backpressure-probe")
            {
                _ = Task.Run(() =>
                {
                    var block = new string('x', 4096);
                    for (var index = 0; index < 1024; index++)
                    {
                        Console.Out.Write(block);
                    }
                });
            }
            var key = Console.ReadKey(intercept: true);
            Console.WriteLine($"KEY:{(int)key.KeyChar:X2}");
            return 0;
        }

        var tests = new (string Name, Func<Task> Run)[]
        {
            ("Portable paths stay under base directory", TestPortablePaths),
            ("IPC resize and exit payloads round-trip", TestPayloadRoundTrip),
            ("IPC stream frames round-trip", TestStreamRoundTrip),
            ("Prompt markers and input ledger prove only conservative idle", TestPromptActivityTracking),
            ("Terminal snapshot boundary has no gaps or duplicates", TestTerminalSnapshotBoundary),
            ("Terminal state overflow invalidates and recovers", TestTerminalStateOverflowRecovery),
            ("Resize invalidates snapshots until matching checkpoint", TestResizeSnapshotRace),
            ("Unsafe authority checkpoints retain an exact bounded replay tail", TestAuthorityCheckpointTail),
            ("Slow and repeated subscribers are cleaned up", TestTerminalSubscriberCleanup),
            ("Terminal resize owner transfers deterministically", TestResizeOwnership),
            ("Input acknowledgements complete success and failure", TestInputAcknowledgements),
            ("Concurrency slots are bounded and released once", TestConcurrencySlots),
            ("Authority readiness is generation-scoped", TestAuthorityReadinessBarrier),
            ("Authority response null fields are parsed without timeout", TestAuthorityResponseParsing),
            ("Remote operations wait for sync and reserve interrupt capacity", TestRemoteOperationGate),
            ("Terminal end follows its final continuous event", TestTerminalDeliveryBoundary),
            ("Host interrupt reaches ConPTY before complete exit", TestHostInterruptIntegration),
            ("Host proves idle only after the CMD root prompt", TestHostCloseAssessmentIntegration),
            ("Host tracks in-process PowerShell commands", TestPowerShellCloseAssessmentIntegration)
        };

        foreach (var test in tests)
        {
            try
            {
                await test.Run();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
                return 1;
            }
        }

        return 0;
    }

    private static Task TestPortablePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "TrayTerminalSmoke", Guid.NewGuid().ToString("N"));
        var paths = new PortablePaths(root);
        paths.EnsureCreated();

        Assert(paths.IsInsideBase(paths.DataDirectory), "Data directory should be inside base.");
        Assert(paths.IsInsideBase(paths.WebView2DataDirectory), "WebView2 directory should be inside base.");
        Assert(
            paths.BackgroundsDirectory == Path.Combine(paths.DataDirectory, "Backgrounds"),
            "Backgrounds directory should be under Data.");
        Assert(
            Directory.Exists(paths.BackgroundsDirectory),
            "Backgrounds directory should be created with the portable data directories.");
        Assert(
            !Directory.Exists(Path.Combine(paths.BaseDirectory, "Backgrounds")),
            "The legacy root Backgrounds directory should not be created.");

        try
        {
            paths.CombineInsideBase("..", "outside.txt");
            throw new InvalidOperationException("Expected escaped path to throw.");
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static Task TestPayloadRoundTrip()
    {
        var resizePayload = IpcProtocol.EncodeResize(132, 40);
        var resize = IpcProtocol.DecodeResize(resizePayload);
        Assert(resize.Columns == 132 && resize.Rows == 40, "Resize payload mismatch.");

        var exitPayload = IpcProtocol.EncodeExit(123);
        Assert(IpcProtocol.DecodeExit(exitPayload) == 123, "Exit payload mismatch.");

        var resizeRequest = IpcProtocol.DecodeResizeRequest(
            IpcProtocol.EncodeResizeRequest(7, 140, 50));
        Assert(
            resizeRequest == (7, 140, 50),
            "Sequenced resize request payload mismatch.");
        var inputResult = IpcProtocol.DecodeInputResult(
            IpcProtocol.EncodeInputResult(8, accepted: false));
        Assert(
            inputResult == (8, false),
            "Input result payload mismatch.");
        var exitStatus = IpcProtocol.DecodeExitStatus(
            IpcProtocol.EncodeExitStatus(123, outputComplete: true));
        Assert(
            exitStatus == (123, true),
            "Exit completion payload mismatch.");
        var closeCheck = IpcProtocol.DecodeCloseCheckResult(
            IpcProtocol.EncodeCloseCheckResult(
                9,
                new TerminalCloseAssessment(
                    TerminalCloseStatus.ActiveProcesses,
                    2)));
        Assert(
            closeCheck == (
                9,
                new TerminalCloseAssessment(
                    TerminalCloseStatus.ActiveProcesses,
                    2)),
            "Close-check result payload mismatch.");
        var closeCheckTimeout = IpcProtocol.DecodeCloseCheckResult(
            IpcProtocol.EncodeCloseCheckResult(
                10,
                new TerminalCloseAssessment(
                    TerminalCloseStatus.CloseCheckTimedOut)));
        Assert(
            closeCheckTimeout == (
                10,
                new TerminalCloseAssessment(
                    TerminalCloseStatus.CloseCheckTimedOut)),
            "Close-check timeout payload mismatch.");

        var text = "hello 你好";
        Assert(IpcProtocol.DecodeText(IpcProtocol.EncodeText(text)) == text, "Text payload mismatch.");

        var inputPayload = IpcProtocol.EncodeInput(42, [1, 2, 3]);
        var input = IpcProtocol.DecodeInput(inputPayload);
        Assert(input.RequestId == 42, "Input request ID mismatch.");
        Assert(
            input.Data.Span.SequenceEqual(new byte[] { 1, 2, 3 }),
            "Input data mismatch.");
        Assert(
            IpcProtocol.DecodeRequestId(IpcProtocol.EncodeRequestId(42)) == 42,
            "Input acknowledgement request ID mismatch.");
        return Task.CompletedTask;
    }

    private static Task TestPromptActivityTracking()
    {
        const string token = "0123456789ABCDEF";
        var tracker = new TerminalActivityTracker();
        var filter = new TerminalPromptFilter(token, tracker.RecordPrompt);
        var marker = Encoding.ASCII.GetBytes(
            TerminalPromptFilter.BuildMarker(token, 0));
        var prefix = Encoding.UTF8.GetBytes("before");
        var suffix = Encoding.UTF8.GetBytes("after");
        var first = prefix.Concat(marker[..7]).ToArray();
        var second = marker[7..].Concat(suffix).ToArray();
        var filtered = filter.Process(first)
            .Concat(filter.Process(second))
            .Concat(filter.Flush())
            .ToArray();
        Assert(
            Encoding.UTF8.GetString(filtered) == "beforeafter",
            "Prompt marker was not removed across output chunks.");

        var initial = tracker.GetSnapshot();
        Assert(
            initial.PromptObserved
                && initial.PendingSubmissions == 0
                && !initial.HasUnresolvedInput,
            "Initial prompt did not establish a clean baseline.");

        tracker.RecordInput("\u001b[O"u8);
        tracker.RecordInput("\u001b[I"u8);
        var afterFocusReports = tracker.GetSnapshot();
        Assert(
            !afterFocusReports.HasUnresolvedInput
                && afterFocusReports.IgnoredFocusReports == 2,
            "xterm focus reports were incorrectly treated as user input.");

        tracker.RecordInput("\u001b[A"u8);
        Assert(
            tracker.GetSnapshot().HasUnresolvedInput,
            "A non-focus escape sequence was incorrectly ignored.");
        tracker.RecordPrompt(activeShellJobs: 0);
        Assert(
            tracker.GetSnapshot().HasUnresolvedInput,
            "A prompt incorrectly cleared unmatched escape input.");

        var mixedInput = new TerminalActivityTracker();
        mixedInput.RecordPrompt(activeShellJobs: 0);
        mixedInput.RecordInput("\u001b[Ox"u8);
        Assert(
            mixedInput.GetSnapshot().HasUnresolvedInput,
            "Input containing a focus report and user text was incorrectly ignored.");

        tracker.RecordInput("first\rsecond\r"u8);
        tracker.RecordPrompt(activeShellJobs: 0);
        var onePending = tracker.GetSnapshot();
        Assert(
            onePending.PendingSubmissions == 1,
            "One prompt incorrectly completed multiple queued commands.");
        tracker.RecordPrompt(activeShellJobs: 0);
        var idle = tracker.GetSnapshot();
        Assert(
            idle.PendingSubmissions == 0
                && !idle.HasUnresolvedInput
                && idle.ActiveShellJobs == 0,
            "Matched prompts did not restore the idle candidate.");

        tracker.RecordInput("partial"u8);
        tracker.RecordPrompt(activeShellJobs: 0);
        Assert(
            tracker.GetSnapshot().HasUnresolvedInput,
            "A prompt incorrectly cleared unmatched input.");

        var startupRace = new TerminalActivityTracker();
        startupRace.RecordInput("queued\r"u8);
        startupRace.RecordPrompt(activeShellJobs: 0);
        Assert(
            startupRace.GetSnapshot().PendingSubmissions == 1,
            "The delayed startup prompt incorrectly completed queued input.");

        var jobTracker = new TerminalActivityTracker();
        var jobFilter = new TerminalPromptFilter(token, jobTracker.RecordPrompt);
        jobFilter.Process(Encoding.ASCII.GetBytes(
            TerminalPromptFilter.BuildMarker(token, 0)));
        jobTracker.RecordInput("Start-Job { work }\r"u8);
        jobFilter.Process(Encoding.ASCII.GetBytes(
            TerminalPromptFilter.BuildMarker(token, 2)));
        var activeJobs = jobTracker.GetSnapshot();
        Assert(
            activeJobs.PendingSubmissions == 0
                && activeJobs.ActiveShellJobs == 2,
            "PowerShell prompt marker did not retain active Job evidence.");
        return Task.CompletedTask;
    }

    private static Task TestAuthorityResponseParsing()
    {
        using var validDocument = JsonDocument.Parse("""
            {
              "ok": true,
              "sequence": "1",
              "snapshot": null,
              "snapshotSequence": null,
              "snapshotCols": null,
              "snapshotRows": null,
              "reason": null,
              "checkpointSafe": true
            }
            """);
        var response = AuthorityResponseProtocol.Parse(validDocument.RootElement);
        Assert(response.Ok && response.Sequence == "1", "Authority response identity mismatch.");
        Assert(
            response.Snapshot is null
                && response.SnapshotSequence is null
                && response.SnapshotCols is null
                && response.SnapshotRows is null,
            "Nullable authority response fields were not preserved.");

        using var invalidDocument = JsonDocument.Parse("""
            {
              "ok": true,
              "sequence": "1",
              "snapshotCols": "80",
              "checkpointSafe": true
            }
            """);
        try
        {
            AuthorityResponseProtocol.Parse(invalidDocument.RootElement);
            throw new InvalidOperationException(
                "Malformed authority response should fail immediately.");
        }
        catch (InvalidDataException)
        {
        }

        return Task.CompletedTask;
    }

    private static async Task TestStreamRoundTrip()
    {
        var pipeName = $"TrayTerminalSmoke-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        var connectClient = client.ConnectAsync();
        await server.WaitForConnectionAsync();
        await connectClient;

        var expected = new IpcMessage(IpcMessageType.Output, [1, 2, 3, 4]);
        var writeTask = IpcProtocol.WriteAsync(server, expected, CancellationToken.None);
        var actual = await IpcProtocol.ReadAsync(client, CancellationToken.None);
        await writeTask;

        var message = actual ?? throw new InvalidOperationException("Expected an IPC message.");
        Assert(message.Type == expected.Type, "IPC message type mismatch.");
        Assert(message.Payload.SequenceEqual(expected.Payload), "IPC payload mismatch.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task TestTerminalSnapshotBoundary()
    {
        using var hub = new TerminalStateHub();
        var first = hub.Publish([1]);
        Assert(first.Sequence == 1, "First output sequence should be one.");
        Assert(
            hub.TryAcceptSnapshot(
                first.Sequence,
                [10],
                new TerminalSize(120, 30)),
            "Checkpoint should be accepted.");
        var second = hub.Publish([2]);

        Assert(hub.TrySubscribe(out var subscription), "Expected a recoverable subscription.");
        using (var activeSubscription = subscription!)
        {
            var initial = activeSubscription.InitialState;
            Assert(initial.Snapshot.Sequence == 1, "Snapshot sequence mismatch.");
            Assert(initial.Replay.Count == 1, "Expected one replay item.");
            Assert(initial.Replay[0].Sequence == second.Sequence, "Replay sequence mismatch.");

            var third = hub.Publish([3]);
            var live = await activeSubscription.Outputs.ReadAsync();
            Assert(live.Sequence == third.Sequence, "Live sequence should follow replay exactly.");
            Assert(
                initial.Replay[0].Sequence + 1 == live.Sequence,
                "Snapshot/replay/live boundary contains a gap or duplicate.");
        }
    }

    private static Task TestTerminalStateOverflowRecovery()
    {
        using var hub = new TerminalStateHub(
            tailByteLimit: 6,
            snapshotByteLimit: 32,
            subscriberQueueCapacity: 2,
            tailItemLimit: 2);
        hub.Publish([1, 2, 3, 4]);
        hub.Publish([5, 6, 7, 8]);
        Assert(!hub.HasRecoverableState, "Overflow should invalidate the old checkpoint.");
        Assert(!hub.TrySubscribe(out _), "Invalid state must reject new subscribers.");
        Assert(
            !hub.TryAcceptSnapshot(0, [], new TerminalSize(120, 30)),
            "A checkpoint before the trimmed boundary must stay invalid.");
        Assert(
            hub.TryAcceptSnapshot(
                hub.LatestSequence,
                [9],
                new TerminalSize(120, 30)),
            "A current checkpoint should recover the state.");
        Assert(hub.HasRecoverableState, "Recovered checkpoint should reopen subscriptions.");

        using var tinyChunkHub = new TerminalStateHub(
            tailByteLimit: 1024,
            snapshotByteLimit: 32,
            subscriberQueueCapacity: 2,
            tailItemLimit: 2);
        tinyChunkHub.Publish([1]);
        tinyChunkHub.Publish([2]);
        tinyChunkHub.Publish([3]);
        Assert(
            !tinyChunkHub.HasRecoverableState,
            "Tiny output chunks must also invalidate state at the item limit.");
        return Task.CompletedTask;
    }

    private static Task TestResizeSnapshotRace()
    {
        using var hub = new TerminalStateHub();
        var output = hub.Publish([1]);
        var originalSize = new TerminalSize(120, 30);
        var resized = new TerminalSize(90, 24);
        Assert(
            hub.TryAcceptSnapshot(output.Sequence, [2], originalSize),
            "Original-size checkpoint should be accepted.");

        hub.InvalidateCheckpoint();
        Assert(
            !hub.TrySubscribe(out _),
            "A resize boundary must close new subscriptions immediately.");
        Assert(
            hub.TryAcceptSnapshot(output.Sequence, [3], resized),
            "A post-resize checkpoint should restore recoverability.");
        Assert(
            hub.TryGetRecoveryState(out var state)
            && state!.Snapshot.Size == resized,
            "Recovered snapshot must carry its generating terminal size.");
        return Task.CompletedTask;
    }

    private static Task TestAuthorityCheckpointTail()
    {
        using var hub = new TerminalStateHub(
            tailByteLimit: 8,
            tailItemLimit: 3,
            snapshotByteLimit: 32,
            initialSize: new TerminalSize(80, 24));
        Assert(
            hub.TryCommitAuthorityEvent(
                1,
                [1],
                null,
                [10],
                new TerminalSize(80, 24),
                out _),
            "Initial authority checkpoint failed.");
        Assert(
            hub.TryCommitAuthorityEvent(
                2,
                [2],
                null,
                null,
                new TerminalSize(80, 24),
                out _),
            "Unsafe output should retain a replay event.");
        Assert(
            hub.TryCommitAuthorityEvent(
                3,
                [],
                new TerminalSize(100, 30),
                null,
                new TerminalSize(100, 30),
                out _),
            "Unsafe resize should retain a replay boundary.");
        Assert(hub.TryGetRecoveryState(out var state), "Recovery state disappeared.");
        Assert(state!.Snapshot.Sequence == 1, "Unsafe events replaced the safe checkpoint.");
        Assert(
            state.Replay.Select(item => item.Sequence).SequenceEqual([2L, 3L]),
            "Recovery replay does not exactly cover the unsafe event tail.");
        Assert(
            state.Replay[1].Resize == new TerminalSize(100, 30),
            "Sequenced resize was lost from the recovery tail.");

        Assert(
            hub.TryCommitAuthorityEvent(
                4,
                [4],
                null,
                [40],
                new TerminalSize(100, 30),
                out _),
            "A later safe checkpoint should collapse the replay tail.");
        Assert(hub.TryGetRecoveryState(out state), "Safe checkpoint was not recoverable.");
        Assert(state!.Snapshot.Sequence == 4, "Safe checkpoint sequence mismatch.");
        Assert(state.Replay.Count == 0, "Safe checkpoint did not clear the covered tail.");

        Assert(
            hub.TryCommitAuthorityEvent(
                5,
                [5, 5, 5, 5],
                null,
                null,
                new TerminalSize(100, 30),
                out _),
            "First bounded unsafe tail event failed.");
        Assert(
            hub.TryCommitAuthorityEvent(
                6,
                [6, 6, 6, 6],
                null,
                null,
                new TerminalSize(100, 30),
                out _),
            "Second bounded unsafe tail event failed.");
        Assert(
            !hub.TryCommitAuthorityEvent(
                7,
                [7],
                null,
                null,
                new TerminalSize(100, 30),
                out _),
            "Unsafe tail was allowed to exceed its hard byte limit.");
        return Task.CompletedTask;
    }

    private static Task TestTerminalSubscriberCleanup()
    {
        using var hub = new TerminalStateHub(
            tailByteLimit: 1024,
            snapshotByteLimit: 1024,
            subscriberQueueCapacity: 1,
            maxSubscribers: 2);
        Assert(hub.TrySubscribe(out var first), "First subscription failed.");
        Assert(hub.TrySubscribe(out var second), "Second subscription failed.");
        Assert(!hub.TrySubscribe(out _), "Per-session subscriber limit was not enforced.");
        second!.Dispose();

        hub.Publish([1]);
        hub.Publish([2]);
        Assert(hub.SubscriberCount == 0, "Slow/full subscribers should be removed.");
        first!.Dispose();

        for (var i = 0; i < 100; i++)
        {
            Assert(hub.TrySubscribe(out var subscription), "Repeated subscription failed.");
            subscription!.Dispose();
        }

        Assert(hub.SubscriberCount == 0, "Repeated disconnects retained subscribers.");
        return Task.CompletedTask;
    }

    private static Task TestResizeOwnership()
    {
        var coordinator = new TerminalSizeCoordinator();
        var local = Guid.NewGuid();
        var remoteOne = Guid.NewGuid();
        var remoteTwo = Guid.NewGuid();

        coordinator.Register(local, TerminalClientKind.Local, false, 100, 30);
        coordinator.Register(remoteOne, TerminalClientKind.Remote, true, 90, 25);
        coordinator.Register(remoteTwo, TerminalClientKind.Remote, true, 80, 20);
        Assert(coordinator.OwnerId == remoteOne, "First remote should own while local is hidden.");

        coordinator.RequestResize(remoteTwo, 70, 18);
        Assert(
            coordinator.CurrentSize == new TerminalSize(90, 25),
            "Non-owner resize must not affect ConPTY.");

        coordinator.SetLocalVisibility(local, true, 120, 40);
        Assert(coordinator.OwnerId == local, "Visible local renderer should take ownership.");
        Assert(
            coordinator.CurrentSize == new TerminalSize(120, 40),
            "Local owner size was not applied.");

        coordinator.SetLocalVisibility(local, false, 120, 40);
        Assert(coordinator.OwnerId == remoteOne, "Hidden local renderer should release ownership.");
        coordinator.Unregister(remoteOne);
        Assert(coordinator.OwnerId == remoteTwo, "Disconnected owner should transfer its lease.");
        Assert(
            coordinator.CurrentSize == new TerminalSize(70, 18),
            "New owner's most recent requested size should be applied.");
        return Task.CompletedTask;
    }

    private static async Task TestInputAcknowledgements()
    {
        var acknowledgements = new InputAckRegistry();
        Assert(
            acknowledgements.TryRegister(1, out var accepted),
            "Input acknowledgement registration failed.");
        Assert(
            acknowledgements.TryComplete(1, accepted: true),
            "Input acknowledgement was not correlated.");
        Assert(await accepted, "Acknowledged input should complete successfully.");
        acknowledgements.Remove(1);

        Assert(
            acknowledgements.TryRegister(2, out var rejected),
            "Second input acknowledgement registration failed.");
        acknowledgements.FailAll();
        Assert(!await rejected, "Session termination should fail pending input.");
        Assert(acknowledgements.Count == 0, "Failed acknowledgements should be released.");
    }

    private static Task TestConcurrencySlots()
    {
        var limiter = new ConcurrencySlotLimiter(2);
        Assert(limiter.TryAcquire(out var first), "First slot should be available.");
        Assert(limiter.TryAcquire(out var second), "Second slot should be available.");
        Assert(!limiter.TryAcquire(out _), "Capacity must reject without queueing.");
        Assert(limiter.ActiveCount == 2, "Active slot count mismatch.");

        first!.Dispose();
        first.Dispose();
        Assert(limiter.ActiveCount == 1, "A lease must release exactly once.");
        Assert(limiter.TryAcquire(out var replacement), "Released slot was not reusable.");
        replacement!.Dispose();
        second!.Dispose();
        Assert(limiter.ActiveCount == 0, "All request slots should be released.");
        return Task.CompletedTask;
    }

    private static async Task TestAuthorityReadinessBarrier()
    {
        var barrier = new AuthorityRecoveryBarrier();
        barrier.StartInitialRecovery();
        var firstGeneration = barrier.Generation;
        var oldWaiter = barrier.WaitReadyAsync();
        var secondGeneration = barrier.RestartRecovery();
        Assert(secondGeneration > firstGeneration, "Recovery generation did not advance.");
        try
        {
            await oldWaiter;
            throw new InvalidOperationException(
                "A waiter crossed a superseded renderer generation.");
        }
        catch (AuthorityGenerationSupersededException)
        {
        }

        Assert(
            !barrier.TrySetReady(firstGeneration),
            "A stale renderer result opened the current barrier.");
        var currentWaiter = barrier.WaitReadyAsync();
        Assert(!currentWaiter.IsCompleted, "Recovering renderer was exposed as ready.");
        Assert(barrier.TrySetReady(secondGeneration), "Current renderer was not accepted.");
        await currentWaiter.WaitAsync(TimeSpan.FromSeconds(1));
        Assert(
            barrier.IsCurrentReady(secondGeneration),
            "Ready state was not scoped to the current generation.");
    }

    private static Task TestRemoteOperationGate()
    {
        var gate = new RemoteOperationGate();
        Assert(!gate.TryBegin(interrupt: false), "Pre-sync input was accepted.");
        Assert(!gate.TryBegin(interrupt: true), "Pre-sync interrupt was accepted.");
        gate.Open();
        for (var index = 0; index < RemoteOperationGate.MaxOperations - 1; index++)
        {
            Assert(gate.TryBegin(interrupt: false), "Ordinary input capacity regressed.");
        }

        Assert(!gate.TryBegin(interrupt: false), "Ordinary input consumed interrupt reserve.");
        Assert(gate.TryBegin(interrupt: true), "Interrupt reserve was unavailable.");
        Assert(!gate.TryBegin(interrupt: true), "Combined operation cap exceeded 64.");
        gate.Close();
        Assert(!gate.TryBegin(interrupt: true), "Disconnected client accepted an operation.");
        for (var index = 0; index < RemoteOperationGate.MaxOperations; index++)
        {
            gate.End();
        }
        Assert(gate.ActiveCount == 0, "Completed operations were retained.");
        return Task.CompletedTask;
    }

    private static Task TestTerminalDeliveryBoundary()
    {
        var tracker = new TerminalDeliveryTracker(initialLatestSequence: 10);
        Assert(!tracker.Reaches(12), "Terminal ended before its live output arrived.");
        Assert(tracker.TryObserve(11), "First live output was rejected.");
        Assert(!tracker.Reaches(12), "Terminal ended before its final output arrived.");
        Assert(tracker.TryObserve(12), "Final live output was rejected.");
        Assert(tracker.Reaches(12), "Terminal end boundary did not follow final output.");
        Assert(!tracker.TryObserve(14), "A sequence gap was accepted.");
        Assert(
            tracker.LastContinuousSequence == 12,
            "A rejected gap changed the continuous delivery boundary.");
        return Task.CompletedTask;
    }

    private static async Task TestHostInterruptIntegration()
    {
        var hostPath = FindSiblingProjectExecutable(
            "TrayTerminal.Host",
            "TrayTerminal.Host.exe");
        var probePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Smoke-test process path is unavailable.");
        Assert(File.Exists(hostPath), $"Host executable not found: {hostPath}");
        Assert(File.Exists(probePath), $"Probe executable not found: {probePath}");

        var outputPipeName = $"TrayTerminalSmoke-Output-{Guid.NewGuid():N}";
        var controlPipeName = $"TrayTerminalSmoke-Control-{Guid.NewGuid():N}";
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var outputPipe = new NamedPipeServerStream(
            outputPipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        await using var controlPipe = new NamedPipeServerStream(
            controlPipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        var arguments = string.Join(
            ' ',
            "--output-pipe", outputPipeName,
            "--control-pipe", controlPipeName,
            "--nonce", nonce,
            "--exe", EncodeHostArgument(probePath),
            "--args", EncodeHostArgument("--backpressure-probe"),
            "--cwd", EncodeHostArgument(AppContext.BaseDirectory),
            "--cols", "80",
            "--rows", "24");
        using var host = Process.Start(new ProcessStartInfo
        {
            FileName = hostPath,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(hostPath)!,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start Host integration test.");

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await Task.WhenAll(
                outputPipe.WaitForConnectionAsync(timeout.Token),
                controlPipe.WaitForConnectionAsync(timeout.Token));
            var hellos = await Task.WhenAll(
                IpcProtocol.ReadAsync(outputPipe, timeout.Token),
                IpcProtocol.ReadAsync(controlPipe, timeout.Token));
            Assert(
                hellos.All(hello => hello is { Type: IpcMessageType.Hello }
                    && IpcProtocol.DecodeText(hello.Value.Payload) == nonce),
                "Dual-pipe Host nonce handshake failed.");

            var probeReady = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var resumeOutput = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var interruptAcked = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var closeCheckReceived = new TaskCompletionSource<TerminalCloseAssessment>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var frozenInputRejected = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var outputTask = Task.Run(async () =>
            {
                using var captured = new MemoryStream();
                var eventOrder = new List<(string Type, long RequestId)>();
                bool? complete = null;
                var paused = false;
                var keyRecorded = false;
                while (complete is null)
                {
                    var message = await IpcProtocol.ReadAsync(outputPipe, timeout.Token)
                        ?? throw new EndOfStreamException("Output pipe ended before completion.");
                    if (message.Type == IpcMessageType.Output)
                    {
                        captured.Write(message.Payload);
                        var eventType = "output";
                        if (!keyRecorded
                            && ContainsProbeKeyOutput(
                                Encoding.UTF8.GetString(captured.ToArray())))
                        {
                            keyRecorded = true;
                            eventType = "key";
                        }
                        eventOrder.Add((eventType, 0));
                        if (Encoding.UTF8.GetString(captured.ToArray())
                            .Contains("READY", StringComparison.Ordinal))
                        {
                            probeReady.TrySetResult();
                            if (!paused)
                            {
                                paused = true;
                                await resumeOutput.Task.WaitAsync(timeout.Token);
                            }
                        }
                    }
                    else if (message.Type == IpcMessageType.ResizeBoundary)
                    {
                        var boundary = IpcProtocol.DecodeResizeRequest(message.Payload);
                        eventOrder.Add(("resize", boundary.RequestId));
                    }
                    else if (message.Type == IpcMessageType.OutputCompleted)
                    {
                        complete = message.Payload is [1];
                    }
                }

                return (
                    Encoding.UTF8.GetString(captured.ToArray()),
                    complete.Value,
                    eventOrder);
            }, timeout.Token);
            var controlTask = Task.Run(async () =>
            {
                bool? interruptAccepted = null;
                (int ExitCode, bool OutputComplete)? exit = null;
                var rejectedResizes = new HashSet<long>();
                while (exit is null)
                {
                    var message = await IpcProtocol.ReadAsync(controlPipe, timeout.Token)
                        ?? throw new EndOfStreamException("Control pipe ended before exit.");
                    if (message.Type == IpcMessageType.InterruptAck)
                    {
                        var result = IpcProtocol.DecodeInputResult(message.Payload);
                        if (result.RequestId == 77)
                        {
                            interruptAccepted = result.Accepted;
                            interruptAcked.TrySetResult(result.Accepted);
                        }
                    }
                    else if (message.Type == IpcMessageType.InputAck)
                    {
                        var result = IpcProtocol.DecodeInputResult(message.Payload);
                        if (result.RequestId == 78)
                        {
                            frozenInputRejected.TrySetResult(!result.Accepted);
                        }
                    }
                    else if (message.Type == IpcMessageType.CloseCheckResult)
                    {
                        var result = IpcProtocol.DecodeCloseCheckResult(
                            message.Payload);
                        if (result.RequestId == 70)
                        {
                            closeCheckReceived.TrySetResult(result.Assessment);
                        }
                    }
                    else if (message.Type == IpcMessageType.ResizeAck)
                    {
                        var result = IpcProtocol.DecodeInputResult(message.Payload);
                        if (!result.Accepted)
                        {
                            rejectedResizes.Add(result.RequestId);
                        }
                    }
                    else if (message.Type == IpcMessageType.Exit)
                    {
                        exit = IpcProtocol.DecodeExitStatus(message.Payload);
                    }
                }

                return (interruptAccepted, exit.Value, rejectedResizes);
            }, timeout.Token);

            await probeReady.Task.WaitAsync(timeout.Token);
            // Let terminal output fill the one-way data pipe while the output
            // reader is intentionally paused. Resize coalescing and interrupt ACK
            // must remain independent from that blocked data path.
            await Task.Delay(500, timeout.Token);
            await IpcProtocol.WriteAsync(
                controlPipe,
                new IpcMessage(
                    IpcMessageType.CloseCheck,
                    IpcProtocol.EncodeRequestId(70)),
                timeout.Token);
            var closeAssessment = await closeCheckReceived.Task.WaitAsync(
                TimeSpan.FromSeconds(3),
                timeout.Token);
            Assert(
                closeAssessment.Status == TerminalCloseStatus.PromptUnavailable,
                "An unsupported shell was not treated as an unknown close state.");
            await IpcProtocol.WriteAsync(
                controlPipe,
                new IpcMessage(
                    IpcMessageType.Input,
                    IpcProtocol.EncodeInput(78, "x"u8)),
                timeout.Token);
            Assert(
                await frozenInputRejected.Task.WaitAsync(
                    TimeSpan.FromSeconds(3),
                    timeout.Token),
                "Host accepted input while a close check was active.");
            await IpcProtocol.WriteAsync(
                controlPipe,
                new IpcMessage(
                    IpcMessageType.CloseCheckEnd,
                    IpcProtocol.EncodeRequestId(70)),
                timeout.Token);
            const long finalResizeRequestId = 20;
            for (long requestId = 1; requestId <= finalResizeRequestId; requestId++)
            {
                await IpcProtocol.WriteAsync(
                    controlPipe,
                    new IpcMessage(
                        IpcMessageType.Resize,
                        IpcProtocol.EncodeResizeRequest(
                            requestId,
                            80 + (int)requestId,
                            24)),
                    timeout.Token);
            }
            await IpcProtocol.WriteAsync(
                controlPipe,
                new IpcMessage(
                    IpcMessageType.Interrupt,
                    IpcProtocol.EncodeRequestId(77)),
                timeout.Token);
            Assert(
                await interruptAcked.Task.WaitAsync(TimeSpan.FromSeconds(3), timeout.Token),
                "Host control ACK was blocked by terminal output backpressure.");
            resumeOutput.TrySetResult();
            var output = await outputTask;
            var control = await controlTask;
            Assert(control.interruptAccepted == true, "Host did not ACK the ConPTY interrupt write.");
            Assert(ContainsProbeKeyOutput(output.Item1),
                "The probe did not receive exactly one ETX byte through ConPTY. "
                + $"Captured tail: {Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    output.Item1[^Math.Min(2048, output.Item1.Length)..]))}");
            Assert(output.Item2, "Host marked final ConPTY output incomplete.");
            Assert(control.Value.ExitCode == 0 && control.Value.OutputComplete,
                "Host exit status did not confirm a complete output drain.");
            Assert(
                Enumerable.Range(1, (int)finalResizeRequestId - 1)
                    .All(requestId => control.rejectedResizes.Contains(requestId)),
                "Coalesced resize requests did not receive explicit false ACKs.");
            var resizeIndex = output.eventOrder.FindIndex(item =>
                item == ("resize", finalResizeRequestId));
            var keyIndex = output.eventOrder.FindIndex(item => item.Type == "key");
            Assert(resizeIndex > 0, "Latest resize boundary was not sequenced after old output.");
            Assert(
                keyIndex > resizeIndex,
                "Output produced after resize crossed ahead of its resize boundary.");
            await host.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            // If an assertion fails before the data reader resumes, killing the
            // Host must still be able to release the probe process tree.
            if (!host.HasExited)
            {
                host.Kill(entireProcessTree: true);
            }
        }
    }

    private static bool ContainsProbeKeyOutput(string output)
    {
        var markerIndex = output.LastIndexOf("KEY:", StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return false;
        }

        var markerTail = output.AsSpan(
            markerIndex,
            Math.Min(256, output.Length - markerIndex));
        return markerTail.IndexOf(":03", StringComparison.Ordinal) >= 0;
    }

    private static Task TestHostCloseAssessmentIntegration()
    {
        var executablePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "cmd.exe");
        return RunShellCloseAssessmentIntegration(
            "cmd",
            executablePath,
            string.Empty,
            "ping -n 3 127.0.0.1 >nul\r");
    }

    private static Task TestPowerShellCloseAssessmentIntegration()
    {
        var executablePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        return RunShellCloseAssessmentIntegration(
            "windows-powershell",
            executablePath,
            "-NoLogo",
            "Start-Sleep -Seconds 2\r");
    }

    private static async Task RunShellCloseAssessmentIntegration(
        string profileId,
        string executablePath,
        string shellArguments,
        string testCommand)
    {
        var hostPath = FindSiblingProjectExecutable(
            "TrayTerminal.Host",
            "TrayTerminal.Host.exe");
        Assert(File.Exists(hostPath), $"Host executable not found: {hostPath}");
        Assert(
            File.Exists(executablePath),
            $"Shell executable not found: {executablePath}");

        var outputPipeName = $"TrayTerminal-Test-Output-{Guid.NewGuid():N}";
        var controlPipeName = $"TrayTerminal-Test-Control-{Guid.NewGuid():N}";
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var outputPipe = new NamedPipeServerStream(
            outputPipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        await using var controlPipe = new NamedPipeServerStream(
            controlPipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        var arguments = string.Join(
            ' ',
            "--output-pipe", outputPipeName,
            "--control-pipe", controlPipeName,
            "--nonce", nonce,
            "--exe", EncodeHostArgument(executablePath),
            "--args", EncodeHostArgument(shellArguments),
            "--cwd", EncodeHostArgument(AppContext.BaseDirectory),
            "--profile-id", EncodeHostArgument(profileId),
            "--cols", "80",
            "--rows", "24");
        using var host = Process.Start(new ProcessStartInfo
        {
            FileName = hostPath,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(hostPath)!,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException(
            $"Could not start {profileId} Host close-assessment test.");

        try
        {
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(20));
            await Task.WhenAll(
                outputPipe.WaitForConnectionAsync(timeout.Token),
                controlPipe.WaitForConnectionAsync(timeout.Token));
            var hellos = await Task.WhenAll(
                IpcProtocol.ReadAsync(outputPipe, timeout.Token),
                IpcProtocol.ReadAsync(controlPipe, timeout.Token));
            Assert(
                hellos.All(hello => hello is { Type: IpcMessageType.Hello }
                    && IpcProtocol.DecodeText(hello.Value.Payload) == nonce),
                "CMD Host nonce handshake failed.");

            var captured = new MemoryStream();
            var outputTask = Task.Run(async () =>
            {
                while (true)
                {
                    var message = await IpcProtocol.ReadAsync(
                        outputPipe,
                        timeout.Token)
                        ?? throw new EndOfStreamException(
                            "CMD output ended before its completion boundary.");
                    if (message.Type == IpcMessageType.Output)
                    {
                        captured.Write(message.Payload);
                    }
                    else if (message.Type == IpcMessageType.OutputCompleted)
                    {
                        return message.Payload is [1];
                    }
                }
            }, timeout.Token);

            async Task<TerminalCloseAssessment> CheckAsync(long requestId)
            {
                await IpcProtocol.WriteAsync(
                    controlPipe,
                    new IpcMessage(
                        IpcMessageType.CloseCheck,
                        IpcProtocol.EncodeRequestId(requestId)),
                    timeout.Token);
                while (true)
                {
                    var message = await IpcProtocol.ReadAsync(
                        controlPipe,
                        timeout.Token)
                        ?? throw new EndOfStreamException(
                            "CMD control pipe ended during close check.");
                    if (message.Type != IpcMessageType.CloseCheckResult)
                    {
                        continue;
                    }

                    var result = IpcProtocol.DecodeCloseCheckResult(
                        message.Payload);
                    if (result.RequestId == requestId)
                    {
                        return result.Assessment;
                    }
                }
            }

            async Task EndCheckAsync(long requestId)
            {
                await IpcProtocol.WriteAsync(
                    controlPipe,
                    new IpcMessage(
                        IpcMessageType.CloseCheckEnd,
                        IpcProtocol.EncodeRequestId(requestId)),
                    timeout.Token);
            }

            async Task<bool> SendInputAsync(long requestId, string input)
            {
                await IpcProtocol.WriteAsync(
                    controlPipe,
                    new IpcMessage(
                        IpcMessageType.Input,
                        IpcProtocol.EncodeInput(
                            requestId,
                            Encoding.UTF8.GetBytes(input))),
                    timeout.Token);
                while (true)
                {
                    var message = await IpcProtocol.ReadAsync(
                        controlPipe,
                        timeout.Token)
                        ?? throw new EndOfStreamException(
                            "CMD control pipe ended before input acknowledgement.");
                    if (message.Type is not IpcMessageType.InputAck)
                    {
                        continue;
                    }

                    var result = IpcProtocol.DecodeInputResult(message.Payload);
                    if (result.RequestId == requestId)
                    {
                        return result.Accepted;
                    }
                }
            }

            TerminalCloseAssessment initial = default;
            long checkRequestId = 100;
            for (var attempt = 0; attempt < 50; attempt++)
            {
                initial = await CheckAsync(checkRequestId);
                await EndCheckAsync(checkRequestId++);
                if (initial.IsIdle)
                {
                    break;
                }
                await Task.Delay(100, timeout.Token);
            }
            var startupOutput = Encoding.UTF8.GetString(captured.ToArray());
            if (startupOutput.Length > 2048)
            {
                startupOutput = startupOutput[^2048..];
            }
            Assert(
                initial.IsIdle,
                $"{profileId} startup prompt never produced an idle proof: {initial.Status}. "
                + $"Output tail: {startupOutput}");

            Assert(
                await SendInputAsync(200, testCommand),
                $"{profileId} test command input was rejected.");
            var active = await CheckAsync(checkRequestId);
            await EndCheckAsync(checkRequestId++);
            Assert(
                active.HasActiveTask,
                $"Running {profileId} command was not active: {active.Status}.");

            TerminalCloseAssessment completed = default;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                await Task.Delay(150, timeout.Token);
                completed = await CheckAsync(checkRequestId);
                await EndCheckAsync(checkRequestId++);
                if (completed.IsIdle)
                {
                    break;
                }
            }
            Assert(
                completed.IsIdle,
                $"Completed {profileId} command never returned to idle: {completed.Status}.");

            Assert(
                await SendInputAsync(201, "exit\r"),
                $"{profileId} exit input was rejected.");
            (int ExitCode, bool OutputComplete)? exit = null;
            while (exit is null)
            {
                var message = await IpcProtocol.ReadAsync(
                    controlPipe,
                    timeout.Token)
                    ?? throw new EndOfStreamException(
                        "CMD control pipe ended before exit status.");
                if (message.Type == IpcMessageType.Exit)
                {
                    exit = IpcProtocol.DecodeExitStatus(message.Payload);
                }
            }

            Assert(await outputTask, "CMD output was not completely drained.");
            Assert(
                exit.Value == (0, true),
                "CMD Host did not report a complete successful exit.");
            Assert(
                !Encoding.UTF8.GetString(captured.ToArray())
                    .Contains(nonce, StringComparison.Ordinal),
                "Shell integration marker leaked into terminal output.");
            await host.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!host.HasExited)
            {
                host.Kill(entireProcessTree: true);
            }
        }
    }

    private static string FindSiblingProjectExecutable(
        string projectName,
        string executableName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !string.Equals(directory.Name, "src", StringComparison.OrdinalIgnoreCase))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new DirectoryNotFoundException("Could not locate the repository src directory.");
        }

        return Path.Combine(
            directory.FullName,
            projectName,
            "bin",
            "x64",
            "Debug",
            "net10.0-windows",
            executableName);
    }

    private static string EncodeHostArgument(string value)
    {
        return value.Length == 0
            ? "."
            : Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }
}
