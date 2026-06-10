using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;

namespace TrayTerminal.Shared.Ipc;

public static class IpcProtocol
{
    private const int HeaderLength = 5;
    public const int MaxPayloadLength = 16 * 1024 * 1024;

    public static async Task WriteAsync(PipeStream stream, IpcMessage message, CancellationToken cancellationToken)
    {
        if (message.Payload.Length > MaxPayloadLength)
        {
            throw new InvalidDataException($"IPC payload length exceeds the limit: {message.Payload.Length}");
        }

        // Frame format: 1 byte message type + 4 byte little-endian payload length + payload.
        // Keeping the protocol binary lets terminal output pass through without text escaping.
        var header = new byte[HeaderLength];
        header[0] = (byte)message.Type;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), message.Payload.Length);

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (message.Payload.Length > 0)
        {
            await stream.WriteAsync(message.Payload, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IpcMessage?> ReadAsync(PipeStream stream, CancellationToken cancellationToken)
    {
        var header = await ReadExactAsync(stream, HeaderLength, cancellationToken).ConfigureAwait(false);
        if (header is null)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        if (length < 0 || length > MaxPayloadLength)
        {
            throw new InvalidDataException($"Invalid IPC payload length: {length}");
        }

        var payload = length == 0
            ? []
            : await ReadExactAsync(stream, length, cancellationToken).ConfigureAwait(false);

        if (payload is null)
        {
            return null;
        }

        return new IpcMessage((IpcMessageType)header[0], payload);
    }

    public static byte[] EncodeText(string text) => Encoding.UTF8.GetBytes(text);

    public static string DecodeText(byte[] payload) => Encoding.UTF8.GetString(payload);

    public static byte[] EncodeResize(int columns, int rows)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, 4), columns);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4, 4), rows);
        return payload;
    }

    public static (int Columns, int Rows) DecodeResize(byte[] payload)
    {
        if (payload.Length != 8)
        {
            throw new InvalidDataException($"Resize payload must be 8 bytes, got {payload.Length}.");
        }

        return (
            BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4, 4)));
    }

    public static byte[] EncodeExit(int exitCode)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(payload, exitCode);
        return payload;
    }

    public static int DecodeExit(byte[] payload)
    {
        if (payload.Length != 4)
        {
            throw new InvalidDataException($"Exit payload must be 4 bytes, got {payload.Length}.");
        }

        return BinaryPrimitives.ReadInt32LittleEndian(payload);
    }

    private static async Task<byte[]?> ReadExactAsync(Stream stream, int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return offset == 0 ? null : throw new EndOfStreamException("IPC stream ended mid-frame.");
            }

            offset += read;
        }

        return buffer;
    }
}
