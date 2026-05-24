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
        if (!File.Exists(initPath))
        {
            return entries;
        }

        foreach (var line in File.ReadAllLines(initPath))
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

            if (string.IsNullOrEmpty(profileType) || string.IsNullOrEmpty(title))
            {
                continue;
            }

            entries.Add(new InitTabEntry(isAdmin, profileType, title));
        }

        return entries;
    }
}
