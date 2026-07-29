using System.IO.Pipes;
using TrayTerminal.Shared;
using TrayTerminal.Shared.Ipc;
using TrayTerminal.Shared.Terminal;

namespace TrayTerminal.SmokeTests;

internal static class Program
{
    private static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("Portable paths stay under base directory", TestPortablePaths),
            ("IPC resize and exit payloads round-trip", TestPayloadRoundTrip),
            ("IPC stream frames round-trip", TestStreamRoundTrip),
            ("Terminal snapshot boundary has no gaps or duplicates", TestTerminalSnapshotBoundary),
            ("Terminal state overflow invalidates and recovers", TestTerminalStateOverflowRecovery),
            ("Resize invalidates snapshots until matching checkpoint", TestResizeSnapshotRace),
            ("Slow and repeated subscribers are cleaned up", TestTerminalSubscriberCleanup),
            ("Terminal resize owner transfers deterministically", TestResizeOwnership),
            ("Input acknowledgements complete success and failure", TestInputAcknowledgements),
            ("Concurrency slots are bounded and released once", TestConcurrencySlots)
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
        Assert(acknowledgements.TryAcknowledge(1), "Input acknowledgement was not correlated.");
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
}
