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
    Error = 8,
    InputAck = 9,
    Interrupt = 10,
    InterruptAck = 11,
    ResizeBoundary = 12,
    OutputCompleted = 13,
    ResizeAck = 14
}
