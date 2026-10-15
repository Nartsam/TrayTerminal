using System.IO.Pipes;
using System.Reflection;
using System.Runtime.CompilerServices;
using TrayTerminal.App.Dialogs;
using TrayTerminal.App.Services;
using TrayTerminal.Shared;
using TrayTerminal.Shared.Ipc;
using TrayTerminal.Shared.Terminal;

namespace TrayTerminal.SmokeTests;

internal static partial class Program
{
    private static TerminalSession CreateUnstartedSession()
    {
        var paths = new PortablePaths(AppContext.BaseDirectory);
        var request = new NewTerminalRequest(
            "test", new TerminalProfile("test", "test", "unused", "", paths.BaseDirectory), false);
        // These tests exercise the real session without starting Host or WebView2.
        // An unstarted session only attaches/detaches the authority failure event;
        // no authority methods are invoked because _authorityCreated stays zero.
        var authority = (TerminalAuthorityHost)RuntimeHelpers.GetUninitializedObject(
            typeof(TerminalAuthorityHost));
        return new TerminalSession(request, paths, new FileLogger(paths, "smoke"), authority);
    }

    private static async Task TestSessionResizeCoalescing()
    {
        await using var session = CreateUnstartedSession();
        var previous = SynchronizationContext.Current;
        var context = new HeldSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var local = session.RegisterTerminalClient(TerminalClientKind.Local, true, 120, 30);
            var remote = session.RegisterTerminalClient(TerminalClientKind.Remote, true, 90, 25);
            context.Drain();
            var updates = new List<TerminalSizeUpdate>();
            session.SizeChanged += (_, update) => updates.Add(update);

            var first = session.RequestResizeAsync(local, 160, 50);
            var duplicate = session.RequestResizeAsync(local, 160, 50);
            context.Drain();
            Assert(first.IsCompletedSuccessfully && duplicate.IsCompletedSuccessfully,
                "Coalesced requests did not complete.");
            Assert(updates.Count == 1 && updates[0].Size == new TerminalSize(160, 50),
                "Duplicate request erased the pending size change.");

            updates.Clear();
            _ = session.RequestResizeAsync(local, 140, 40);
            _ = session.RequestResizeAsync(remote, 95, 26);
            context.Drain();
            Assert(updates.Count == 1 && updates[0].Size == new TerminalSize(140, 40),
                "Non-owner request erased the owner's pending size change.");
            Assert(session.TryGetRecoveryState(out var recovery)
                && recovery!.Snapshot.Size == new TerminalSize(140, 40),
                "Applied bootstrap size disagrees with the coordinator.");

            updates.Clear();
            _ = session.SetLocalClientVisibilityAsync(local, false, 140, 40);
            _ = session.RequestResizeAsync(remote, 95, 26);
            context.Drain();
            Assert(updates.Count == 1 && updates[0].OwnerId == remote
                && updates[0].OwnerChanged && updates[0].Size == new TerminalSize(95, 26),
                "A duplicate request erased the ownership transfer.");

            updates.Clear();
            var stale = session.GetCurrentSizeUpdate();
            _ = session.RequestResizeAsync(remote, 100, 28);
            var queue = typeof(TerminalSession).GetMethod(
                "QueueSizeUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!;
            _ = queue.Invoke(session, [stale]);
            context.Drain();
            Assert(updates.Count == 1 && updates[0].Size == new TerminalSize(100, 28),
                "An out-of-order older revision replaced the newest request.");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static async Task TestPartialInputAcknowledgement()
    {
        await using var session = CreateUnstartedSession();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var name = $"TrayTerminal-PartialInput-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var host = new NamedPipeClientStream(
            ".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(deadline.Token), host.ConnectAsync(deadline.Token));
        typeof(TerminalSession).GetField("_controlPipe", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(session, server);
        var acknowledgements = (InputAckRegistry)typeof(TerminalSession).GetField(
            "_pendingInputs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;

        var input = session.TrySendInputAsync(new string('x', 65537), deadline.Token);
        var first = await IpcProtocol.ReadAsync(host, deadline.Token);
        Assert(first?.Type == IpcMessageType.Input, "Expected the first paste chunk.");
        var prefix = IpcProtocol.DecodeInput(first!.Value.Payload);
        Assert(prefix.Data.Length == 65536, "Unexpected paste chunk size.");
        var interrupt = session.TryInterruptAsync(deadline.Token);
        var interruptFrame = await IpcProtocol.ReadAsync(host, deadline.Token);
        Assert(interruptFrame?.Type == IpcMessageType.Interrupt, "Interrupt did not bypass input ACK wait.");

        // A successful Host ACK means this prefix was written, even if generation
        // cancellation prevents sending the remainder of the paste.
        acknowledgements.TryComplete(prefix.RequestId, true);
        acknowledgements.TryComplete(IpcProtocol.DecodeRequestId(interruptFrame!.Value.Payload), true);
        Assert(!await input, "Interrupted partial input was reported as fully accepted.");
        Assert(await interrupt, "Interrupt ACK was lost.");
    }

    private sealed class HeldSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _callbacks = new();

        public override void Post(SendOrPostCallback callback, object? state)
        {
            _callbacks.Enqueue((callback, state));
        }

        public void Drain()
        {
            while (_callbacks.TryDequeue(out var next))
            {
                next.Callback(next.State);
            }
        }
    }
}
