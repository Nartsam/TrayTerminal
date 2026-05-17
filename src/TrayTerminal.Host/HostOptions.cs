using System.Text;

namespace TrayTerminal.Host;

internal sealed class HostOptions
{
    public required string PipeName { get; init; }

    public required string Nonce { get; init; }

    public required string ExecutablePath { get; init; }

    public required string Arguments { get; init; }

    public required string WorkingDirectory { get; init; }

    public int Columns { get; init; } = 120;

    public int Rows { get; init; } = 30;

    public static HostOptions Parse(string[] args)
    {
        // This parser accepts only "--key value" pairs because the App already encodes
        // values that may contain spaces; keep it deliberately small for UAC relaunches.
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = args[index][2..];
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for argument: {args[index]}");
            }

            values[key] = args[++index];
        }

        return new HostOptions
        {
            PipeName = Required(values, "pipe"),
            Nonce = Required(values, "nonce"),
            ExecutablePath = DecodeRequired(values, "exe"),
            Arguments = DecodeRequired(values, "args"),
            WorkingDirectory = DecodeRequired(values, "cwd"),
            Columns = int.TryParse(values.GetValueOrDefault("cols"), out var columns) ? Math.Clamp(columns, 20, 500) : 120,
            Rows = int.TryParse(values.GetValueOrDefault("rows"), out var rows) ? Math.Clamp(rows, 5, 200) : 30
        };
    }

    public static string EncodeArgument(string value)
    {
        return value.Length == 0 ? "." : Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    private static string Required(Dictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing required argument: --{key}");
    }

    private static string DecodeRequired(Dictionary<string, string> values, string key)
    {
        var value = Required(values, key);
        if (value == ".")
        {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }
}
