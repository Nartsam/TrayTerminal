using System.IO;

namespace TrayTerminal.App.Services;

public sealed record TabPreset(
    string? WorkingDirectory,
    string? RunCommand,
    string? FillCommand,
    string? BackgroundPath,
    int Cover);

public static class TabPresetConfig
{
    public static Dictionary<string, TabPreset> Load(string configPath)
    {
        var result = new Dictionary<string, TabPreset>(StringComparer.Ordinal);
        string[] lines;
        try
        {
            if (!File.Exists(configPath))
            {
                return result;
            }

            lines = File.ReadAllLines(configPath);
        }
        catch (Exception exception) when (IsConfigurationException(exception))
        {
            return result;
        }

        string? currentName = null;
        string? cd = null;
        string? run = null;
        string? fill = null;
        string? bg = null;
        var cover = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();

            // Blank lines are ignored — sections are committed when the next section
            // header appears or at end of file. This way blank lines inside a
            // section do not silently drop the remaining properties.
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!line.StartsWith(' ') && !line.StartsWith('\t') && line.EndsWith(':'))
            {
                if (currentName is not null)
                {
                    result[currentName] = new TabPreset(cd, run, fill, bg, cover);
                }
                currentName = line[..^1].Trim();
                if (string.IsNullOrWhiteSpace(currentName))
                {
                    currentName = null;
                    continue;
                }

                cd = null;
                run = null;
                fill = null;
                bg = null;
                cover = 0;
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
                    case "cover":
                        cover = ParseCover(value);
                        break;
                }
            }
        }

        if (currentName is not null)
        {
            result[currentName] = new TabPreset(cd, run, fill, bg, cover);
        }

        return result;
    }

    private static bool IsConfigurationException(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException;
    }

    private static int ParseCover(string? value)
    {
        return int.TryParse(value, out var cover) && cover is >= 0 and <= 100 ? cover : 0;
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
