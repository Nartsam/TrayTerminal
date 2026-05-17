namespace TrayTerminal.Shared.Terminal;

public static class TerminalProfileCatalog
{
    public static IReadOnlyList<TerminalProfile> Detect(PortablePaths paths)
    {
        var profiles = new List<TerminalProfile>();

        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var cmd = Path.Combine(systemRoot, "System32", "cmd.exe");
        if (File.Exists(cmd))
        {
            profiles.Add(new TerminalProfile("cmd", "CMD", cmd, string.Empty, paths.BaseDirectory));
        }

        var powershell = Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(powershell))
        {
            profiles.Add(new TerminalProfile("windows-powershell", "Windows PowerShell", powershell, "-NoLogo", paths.BaseDirectory));
        }

        var pwsh = FindOnPath("pwsh.exe");
        if (pwsh is not null)
        {
            profiles.Add(new TerminalProfile("pwsh", "PowerShell 7", pwsh, "-NoLogo", paths.BaseDirectory));
        }

        return profiles;
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }
}
