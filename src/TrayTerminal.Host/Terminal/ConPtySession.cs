using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TrayTerminal.Host.Terminal;

internal sealed class ConPtySession : IDisposable
{
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateSuspended = 0x00000004;
    private const uint ExtendedStartupInfoPresent = 0x00080000;

    private IntPtr _pseudoConsole;
    private readonly Process _process;
    private readonly SafeFileHandle? _jobHandle;
    private readonly bool _promptTrackingEnabled;
    private bool _disposed;

    private ConPtySession(
        IntPtr pseudoConsole,
        Stream input,
        Stream output,
        Process process,
        SafeFileHandle? jobHandle,
        bool promptTrackingEnabled)
    {
        _pseudoConsole = pseudoConsole;
        Input = input;
        Output = output;
        _process = process;
        _jobHandle = jobHandle;
        _promptTrackingEnabled = promptTrackingEnabled;
    }

    public int ProcessId => _process.Id;

    public Stream Input { get; }

    public Stream Output { get; }

    public bool PromptTrackingEnabled => _promptTrackingEnabled;

    public static ConPtySession Start(
        string executablePath,
        string arguments,
        string workingDirectory,
        string profileId,
        string markerToken,
        int columns,
        int rows)
    {
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("Terminal executable was not found.", executablePath);
        }

        Directory.CreateDirectory(workingDirectory);

        SafeFileHandle? inputRead = null;
        SafeFileHandle? inputWrite = null;
        SafeFileHandle? outputRead = null;
        SafeFileHandle? outputWrite = null;
        SafeFileHandle? jobHandle = null;
        Stream? inputStream = null;
        Stream? outputStream = null;
        Process? process = null;
        var pseudoConsole = IntPtr.Zero;
        var attributeList = IntPtr.Zero;
        var environmentBlock = IntPtr.Zero;
        var processInfo = default(NativeMethods.PROCESS_INFORMATION);
        var success = false;
        try
        {
            var launch = ShellIntegration.Prepare(
                profileId,
                arguments,
                markerToken);
            if (launch.CommandPrompt is not null)
            {
                environmentBlock = NativeMethods.CreateEnvironmentBlock(
                    launch.CommandPrompt);
            }

            ThrowIfFalse(NativeMethods.CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0), "Create input pipe");
            ThrowIfFalse(NativeMethods.CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0), "Create output pipe");

            var size = new NativeMethods.COORD((short)columns, (short)rows);
            ThrowIfFailed(NativeMethods.CreatePseudoConsole(size, inputRead, outputWrite, 0, out pseudoConsole), "CreatePseudoConsole");

            inputRead.Dispose();
            inputRead = null;
            outputWrite.Dispose();
            outputWrite = null;

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

            var commandLine = NativeMethods.BuildCommandLine(
                executablePath,
                launch.Arguments);
            ThrowIfFalse(
                NativeMethods.CreateProcess(
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    // The root process cannot create an untracked child between
                    // CreateProcess and its Job Object assignment.
                    ExtendedStartupInfoPresent
                        | CreateUnicodeEnvironment
                        | CreateSuspended,
                    environmentBlock,
                    workingDirectory,
                    ref startupInfo,
                    out processInfo),
                "CreateProcess");

            jobHandle = NativeMethods.TryCreateKillOnCloseJob();
            if (jobHandle is not null)
            {
                if (!NativeMethods.AssignProcessToJobObject(
                        jobHandle,
                        processInfo.hProcess))
                {
                    jobHandle.Dispose();
                    jobHandle = null;
                }
            }

            // CreatePipe returns synchronous handles. Passing isAsync:false avoids
            // FileStream's overlapped-I/O requirement for asynchronous operations.
            inputStream = new FileStream(inputWrite, FileAccess.Write, 4096, false);
            inputWrite = null;
            outputStream = new FileStream(outputRead, FileAccess.Read, 4096, false);
            outputRead = null;

            process = Process.GetProcessById((int)processInfo.dwProcessId);
            ThrowIfFalse(
                NativeMethods.ResumeThread(processInfo.hThread) != uint.MaxValue,
                "ResumeThread");
            var session = new ConPtySession(
                pseudoConsole,
                inputStream,
                outputStream,
                process,
                jobHandle,
                launch.PromptTrackingEnabled);
            pseudoConsole = IntPtr.Zero;
            inputStream = null;
            outputStream = null;
            process = null;
            jobHandle = null;
            success = true;
            return session;
        }
        catch
        {
            if (processInfo.hProcess != IntPtr.Zero)
            {
                NativeMethods.TerminateProcess(processInfo.hProcess, 1);
            }
            inputStream?.Dispose();
            outputStream?.Dispose();
            process?.Dispose();
            jobHandle?.Dispose();
            if (pseudoConsole != IntPtr.Zero)
            {
                NativeMethods.ClosePseudoConsole(pseudoConsole);
            }

            throw;
        }
        finally
        {
            if (!success)
            {
                inputRead?.Dispose();
                inputWrite?.Dispose();
                outputRead?.Dispose();
                outputWrite?.Dispose();
            }

            if (attributeList != IntPtr.Zero)
            {
                NativeMethods.DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            if (environmentBlock != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environmentBlock);
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

    public bool TryGetActiveProcessCount(out uint activeProcesses)
    {
        if (_disposed || _jobHandle is null || _jobHandle.IsInvalid)
        {
            activeProcesses = 0;
            return false;
        }

        return NativeMethods.TryGetActiveProcessCount(
            _jobHandle,
            out activeProcesses);
    }

    public void CompleteProcessExit()
    {
        // Closing HPCON after the client exits releases the output writer owned
        // by ConPTY. The App-facing output pump can then read the final buffered
        // bytes and observe EOF instead of being canceled at process exit.
        try
        {
            Input.Dispose();
        }
        catch
        {
        }

        var pseudoConsole = Interlocked.Exchange(
            ref _pseudoConsole,
            IntPtr.Zero);
        if (pseudoConsole != IntPtr.Zero)
        {
            NativeMethods.ClosePseudoConsole(pseudoConsole);
        }
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
        var pseudoConsole = Interlocked.Exchange(
            ref _pseudoConsole,
            IntPtr.Zero);
        if (pseudoConsole != IntPtr.Zero)
        {
            NativeMethods.ClosePseudoConsole(pseudoConsole);
        }
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
