namespace TrayTerminal.Shared.Ipc;

public enum IpcMessageType : byte
{
    Hello = 1,
    Input = 2,
    Output = 3,
    Resize = 4,
    Exit = 5,
    Kill = 6,
    Heartbeat = 7,
    Error = 8
}
