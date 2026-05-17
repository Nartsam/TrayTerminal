namespace TrayTerminal.Shared.Ipc;

public readonly record struct IpcMessage(IpcMessageType Type, byte[] Payload)
{
    public static IpcMessage Empty(IpcMessageType type) => new(type, []);
}
