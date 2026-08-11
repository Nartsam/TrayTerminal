using System.IO;

namespace TrayTerminal.App.Services;

public sealed record InitTabEntry(
    bool RunAsAdministrator,
    string ProfileType,
    string Title);

public static class InitConfig
{
    public static List<InitTabEntry> Load(string initPath)
    {
        var entries = new List<InitTabEntry>();
        string[] lines;
        try
        {
            if (!File.Exists(initPath))
            {
                return entries;
            }

            lines = File.ReadAllLines(initPath);
        }
        catch (Exception exception) when (IsConfigurationException(exception))
        {
            return entries;
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split('/', 3);
            if (parts.Length < 3)
            {
                continue;
            }

            var isAdmin = string.Equals(parts[0].Trim(), "admin", StringComparison.OrdinalIgnoreCase);
            var profileType = parts[1].Trim();
            var title = parts[2].Trim();

            if (string.IsNullOrEmpty(profileType)
                || string.IsNullOrEmpty(title)
                || title.Any(char.IsControl))
            {
                continue;
            }

            entries.Add(new InitTabEntry(isAdmin, profileType, title));
        }

        return entries;
    }

    private static bool IsConfigurationException(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException;
    }
}
