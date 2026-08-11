using System.Text.Json;

namespace TrayTerminal.Shared.Terminal;

public sealed record AuthorityResponsePayload(
    bool Ok,
    string? Sequence,
    string? Snapshot,
    string? SnapshotSequence,
    int? SnapshotCols,
    int? SnapshotRows,
    string? Reason,
    bool CheckpointSafe);

public static class AuthorityResponseProtocol
{
    public static AuthorityResponsePayload Parse(JsonElement root)
    {
        return new AuthorityResponsePayload(
            ReadRequiredBoolean(root, "ok"),
            ReadOptionalString(root, "sequence"),
            ReadOptionalString(root, "snapshot"),
            ReadOptionalString(root, "snapshotSequence"),
            ReadOptionalInt32(root, "snapshotCols"),
            ReadOptionalInt32(root, "snapshotRows"),
            ReadOptionalString(root, "reason"),
            ReadOptionalBoolean(root, "checkpointSafe"));
    }

    private static bool ReadRequiredBoolean(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new InvalidDataException(
                $"Authority response property '{propertyName}' must be a boolean.");
        }

        return value.GetBoolean();
    }

    private static string? ReadOptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                $"Authority response property '{propertyName}' must be a string or null.");
        }

        return value.GetString();
    }

    private static int? ReadOptionalInt32(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var result))
        {
            throw new InvalidDataException(
                $"Authority response property '{propertyName}' must be a 32-bit integer or null.");
        }

        return result;
    }

    private static bool ReadOptionalBoolean(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        if (value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new InvalidDataException(
                $"Authority response property '{propertyName}' must be a boolean or null.");
        }

        return value.GetBoolean();
    }
}
