using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TrayTerminal.Host.Terminal;

internal sealed class ConPtySession : IDisposable
{
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;

    private readonly IntPtr _pseudoConsole;
    private readonly Process _process;
    private readonly SafeFileHandle? _jobHandle;
    private bool _disposed;

    private ConPtySession(
        IntPtr pseudoConsole,
        Stream input,
        Stream output,
        Process process,
        SafeFileHandle? jobHandle)
    {
        _pseudoConsole = pseudoConsole;
        Input = input;
        Output = output;
        _process = process;
        _jobHandle = jobHandle;
    }

    public int ProcessId => _process.Id;

    public Stream Input { get; }

    public Stream Output { get; }

    public static ConPtySession Start(string executablePath, string arguments, string workingDirectory, int columns, int rows)
    {
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("Terminal executable was not found.", executablePath);
        }

        Directory.CreateDirectory(workingDirectory);

        ThrowIfFalse(NativeMethods.CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0), "Create input pipe");
        ThrowIfFalse(NativeMethods.CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0), "Create output pipe");

        var size = new NativeMethods.COORD((short)columns, (short)rows);
        ThrowIfFailed(NativeMethods.CreatePseudoConsole(size, inputRead, outputWrite, 0, out var pseudoConsole), "CreatePseudoConsole");

        inputRead.Dispose();
        outputWrite.Dispose();

        var attributeList = IntPtr.Zero;
        var processInfo = default(NativeMethods.PROCESS_INFORMATION);
        try
        {
            var startupInfo = new NativeMethods.STARTUPINFOEX();
            startupInfo.StartupInfo.cb = Marshal.SizeOf<NativeMethods.STARTUPINFOEX>();

            var attributeListSize = IntPtr.Zero;
            NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
            attributeList = Marshal.AllocHGlobal(attributeListSize);
            ThrowIfFalse(
                NativeMethods.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize),
                "InitializeProcThreadAttributeList");

            ThrowIfFalse(
                NativeMethods.UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    (IntPtr)NativeMethods.ProcThreadAttributePseudoConsole,
                    pseudoConsole,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero),
                "UpdateProcThreadAttribute");

            startupInfo.lpAttributeList = attributeList;

            var commandLine = NativeMethods.BuildCommandLine(executablePath, arguments);
            ThrowIfFalse(
                NativeMethods.CreateProcess(
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                    IntPtr.Zero,
                    workingDirectory,
                    ref startupInfo,
                    out processInfo),
                "CreateProcess");

            var jobHandle = NativeMethods.TryCreateKillOnCloseJob();
            if (jobHandle is not null)
            {
                NativeMethods.AssignProcessToJobObject(jobHandle, processInfo.hProcess);
            }

            var process = Process.GetProcessById((int)processInfo.dwProcessId);
            return new ConPtySession(
                pseudoConsole,
                // CreatePipe returns synchronous handles. Passing isAsync:false avoids
                // FileStream's overlapped-I/O requirement for asynchronous operations.
                new FileStream(inputWrite, FileAccess.Write, 4096, false),
                new FileStream(outputRead, FileAccess.Read, 4096, false),
                process,
                jobHandle);
        }
        catch
        {
            NativeMethods.ClosePseudoConsole(pseudoConsole);
            inputWrite.Dispose();
            outputRead.Dispose();
            throw;
        }
        finally
        {
            if (attributeList != IntPtr.Zero)
            {
                NativeMethods.DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            if (processInfo.hThread != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(processInfo.hThread);
            }

            if (processInfo.hProcess != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(processInfo.hProcess);
            }
        }
    }

    public void Resize(int columns, int rows)
    {
        columns = Math.Clamp(columns, 20, 500);
        rows = Math.Clamp(rows, 5, 200);
        ThrowIfFailed(NativeMethods.ResizePseudoConsole(_pseudoConsole, new NativeMethods.COORD((short)columns, (short)rows)), "ResizePseudoConsole");
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return _process.ExitCode;
    }

    public void KillProcessTree()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Output.Dispose();
        Input.Dispose();
        _process.Dispose();
        _jobHandle?.Dispose();
        NativeMethods.ClosePseudoConsole(_pseudoConsole);
    }

    private static void ThrowIfFalse(bool result, string operation)
    {
        if (!result)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), operation);
        }
    }

    private static void ThrowIfFailed(int hresult, string operation)
    {
        if (hresult < 0)
        {
            Marshal.ThrowExceptionForHR(hresult);
        }
        else if (hresult != 0)
        {
            throw new Win32Exception(hresult, operation);
        }
    }
}
