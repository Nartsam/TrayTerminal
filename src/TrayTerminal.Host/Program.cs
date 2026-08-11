using System.IO.Pipes;
using TrayTerminal.Host.Terminal;
using TrayTerminal.Shared;
using TrayTerminal.Shared.Ipc;

namespace TrayTerminal.Host;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var paths = new PortablePaths(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory);
        paths.EnsureCreated();
        Directory.SetCurrentDirectory(paths.BaseDirectory);

        FileLogger.LoadSettings(paths);
        var logger = new FileLogger(paths, "host");

        try
        {
            var options = HostOptions.Parse(args);
            using var cts = new CancellationTokenSource();
            using var outputPipe = new NamedPipeClientStream(
                ".",
                options.OutputPipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            using var controlPipe = new NamedPipeClientStream(
                ".",
                options.ControlPipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            await Task.WhenAll(
                outputPipe.ConnectAsync(15_000, cts.Token),
                controlPipe.ConnectAsync(15_000, cts.Token)).ConfigureAwait(false);
            await IpcProtocol.WriteAsync(
                outputPipe,
                new IpcMessage(IpcMessageType.Hello, IpcProtocol.EncodeText(options.Nonce)),
                cts.Token).ConfigureAwait(false);
            await IpcProtocol.WriteAsync(
                controlPipe,
                new IpcMessage(IpcMessageType.Hello, IpcProtocol.EncodeText(options.Nonce)),
                cts.Token).ConfigureAwait(false);

            try
            {
                using var session = TerminalHostSession.Start(options, logger);
                return await session.RunAsync(
                    outputPipe,
                    controlPipe,
                    cts.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.Error(exception, "Terminal session failed after IPC handshake.");
                await IpcProtocol.WriteAsync(
                    controlPipe,
                    new IpcMessage(IpcMessageType.Error, IpcProtocol.EncodeText(exception.Message)),
                    CancellationToken.None).ConfigureAwait(false);
                return 1;
            }
        }
        catch (Exception exception)
        {
            logger.Error(exception, "Host failed.");
            return 1;
        }
    }
}
