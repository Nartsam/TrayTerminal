using System.IO.Pipes;
using TrayTerminal.Shared;
using TrayTerminal.Shared.Ipc;

namespace TrayTerminal.SmokeTests;

internal static class Program
{
    private static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("Portable paths stay under base directory", TestPortablePaths),
            ("IPC resize and exit payloads round-trip", TestPayloadRoundTrip),
            ("IPC stream frames round-trip", TestStreamRoundTrip)
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
}
