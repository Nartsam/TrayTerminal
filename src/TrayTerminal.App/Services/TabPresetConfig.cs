using System.IO;

namespace TrayTerminal.App.Services;

public sealed record TabPreset(
    string? WorkingDirectory,
    string? RunCommand,
    string? FillCommand,
    string? BackgroundPath);

public static class TabPresetConfig
{
    public static Dictionary<string, TabPreset> Load(string configPath)
    {
        var result = new Dictionary<string, TabPreset>(StringComparer.Ordinal);
        if (!File.Exists(configPath))
        {
            return result;
        }

        var lines = File.ReadAllLines(configPath);
        string? currentName = null;
        string? cd = null;
        string? run = null;
        string? fill = null;
        string? bg = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();

            if (string.IsNullOrWhiteSpace(line))
            {
                if (currentName is not null)
                {
                    result[currentName] = new TabPreset(cd, run, fill, bg);
                    currentName = null;
                    cd = null;
                    run = null;
                    fill = null;
                    bg = null;
                }
                continue;
            }

            if (!line.StartsWith(' ') && !line.StartsWith('\t') && line.EndsWith(':'))
            {
                if (currentName is not null)
                {
                    result[currentName] = new TabPreset(cd, run, fill, bg);
                }
                currentName = line[..^1];
                cd = null;
                run = null;
                fill = null;
                bg = null;
            }
            else if (currentName is not null)
            {
                var trimmed = line.TrimStart();
                var colonIndex = trimmed.IndexOf(':');
                if (colonIndex <= 0)
                {
                    continue;
                }

                var key = trimmed[..colonIndex].Trim();
                var value = trimmed[(colonIndex + 1)..].Trim();
                value = Unquote(value);

                if (string.IsNullOrEmpty(value))
                {
                    value = null;
                }

                switch (key)
                {
                    case "cd":
                        cd = value;
                        break;
                    case "run":
                        run = value;
                        break;
                    case "fill":
                        fill = value;
                        break;
                    case "bg":
                        bg = value;
                        break;
                }
            }
        }

        if (currentName is not null)
        {
            result[currentName] = new TabPreset(cd, run, fill, bg);
        }

        return result;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1];
        }
        return value;
    }
}
