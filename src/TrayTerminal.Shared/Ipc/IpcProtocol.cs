using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using TrayTerminal.Shared.Terminal;

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

    public static byte[] EncodeInput(long requestId, ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxPayloadLength - sizeof(long))
        {
            throw new InvalidDataException($"Input payload length exceeds the limit: {data.Length}");
        }

        var payload = new byte[sizeof(long) + data.Length];
        BinaryPrimitives.WriteInt64LittleEndian(payload, requestId);
        data.CopyTo(payload.AsSpan(sizeof(long)));
        return payload;
    }

    public static (long RequestId, ReadOnlyMemory<byte> Data) DecodeInput(byte[] payload)
    {
        if (payload.Length < sizeof(long))
        {
            throw new InvalidDataException($"Input payload must be at least 8 bytes, got {payload.Length}.");
        }

        return (
            BinaryPrimitives.ReadInt64LittleEndian(payload),
            payload.AsMemory(sizeof(long)));
    }

    public static byte[] EncodeRequestId(long requestId)
    {
        var payload = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(payload, requestId);
        return payload;
    }

    public static long DecodeRequestId(byte[] payload)
    {
        if (payload.Length != sizeof(long))
        {
            throw new InvalidDataException($"Request ID payload must be 8 bytes, got {payload.Length}.");
        }

        return BinaryPrimitives.ReadInt64LittleEndian(payload);
    }

    public static byte[] EncodeInputResult(long requestId, bool accepted)
    {
        var payload = new byte[sizeof(long) + sizeof(byte)];
        BinaryPrimitives.WriteInt64LittleEndian(payload, requestId);
        payload[sizeof(long)] = accepted ? (byte)1 : (byte)0;
        return payload;
    }

    public static (long RequestId, bool Accepted) DecodeInputResult(byte[] payload)
    {
        if (payload.Length != sizeof(long) + sizeof(byte)
            || payload[sizeof(long)] > 1)
        {
            throw new InvalidDataException(
                $"Input result payload must be a request ID and boolean, got {payload.Length} bytes.");
        }

        return (
            BinaryPrimitives.ReadInt64LittleEndian(payload),
            payload[sizeof(long)] == 1);
    }

    public static byte[] EncodeCloseCheckResult(
        long requestId,
        TerminalCloseAssessment assessment)
    {
        var payload = new byte[sizeof(long) + sizeof(byte) + sizeof(int)];
        BinaryPrimitives.WriteInt64LittleEndian(payload, requestId);
        payload[sizeof(long)] = (byte)assessment.Status;
        BinaryPrimitives.WriteInt32LittleEndian(
            payload.AsSpan(sizeof(long) + sizeof(byte)),
            Math.Max(0, assessment.Detail));
        return payload;
    }

    public static (long RequestId, TerminalCloseAssessment Assessment)
        DecodeCloseCheckResult(byte[] payload)
    {
        if (payload.Length != sizeof(long) + sizeof(byte) + sizeof(int)
            || !Enum.IsDefined((TerminalCloseStatus)payload[sizeof(long)]))
        {
            throw new InvalidDataException(
                $"Close-check result payload is invalid, got {payload.Length} bytes.");
        }

        var detail = BinaryPrimitives.ReadInt32LittleEndian(
            payload.AsSpan(sizeof(long) + sizeof(byte)));
        if (detail < 0)
        {
            throw new InvalidDataException("Close-check detail cannot be negative.");
        }

        return (
            BinaryPrimitives.ReadInt64LittleEndian(payload),
            new TerminalCloseAssessment(
                (TerminalCloseStatus)payload[sizeof(long)],
                detail));
    }

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

    public static byte[] EncodeResizeRequest(long requestId, int columns, int rows)
    {
        var payload = new byte[sizeof(long) + sizeof(int) * 2];
        BinaryPrimitives.WriteInt64LittleEndian(payload, requestId);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(sizeof(long)), columns);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(sizeof(long) + sizeof(int)), rows);
        return payload;
    }

    public static (long RequestId, int Columns, int Rows) DecodeResizeRequest(byte[] payload)
    {
        if (payload.Length != sizeof(long) + sizeof(int) * 2)
        {
            throw new InvalidDataException(
                $"Resize request payload must be 16 bytes, got {payload.Length}.");
        }

        return (
            BinaryPrimitives.ReadInt64LittleEndian(payload),
            BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(sizeof(long))),
            BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(sizeof(long) + sizeof(int))));
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

    public static byte[] EncodeExitStatus(int exitCode, bool outputComplete)
    {
        var payload = new byte[sizeof(int) + sizeof(byte)];
        BinaryPrimitives.WriteInt32LittleEndian(payload, exitCode);
        payload[sizeof(int)] = outputComplete ? (byte)1 : (byte)0;
        return payload;
    }

    public static (int ExitCode, bool OutputComplete) DecodeExitStatus(byte[] payload)
    {
        if (payload.Length != sizeof(int) + sizeof(byte)
            || payload[sizeof(int)] > 1)
        {
            throw new InvalidDataException(
                $"Exit status payload must be an exit code and boolean, got {payload.Length} bytes.");
        }

        return (
            BinaryPrimitives.ReadInt32LittleEndian(payload),
            payload[sizeof(int)] == 1);
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
