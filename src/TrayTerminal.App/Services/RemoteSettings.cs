using System.IO;
using TrayTerminal.Shared;

namespace TrayTerminal.App.Services;

public static class RemoteSettings
{
    private static bool _loaded;

    public static int Port { get; private set; } = 8848;

    public static string? Token { get; private set; }

    public static HashSet<string> AllowedTabs { get; private set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Reads <c>Data\Config\settings.txt</c> and extracts remote-access settings.
    /// Safe to call multiple times — only the first call has any effect.
    /// </summary>
    public static void Load(PortablePaths paths)
    {
        lock (typeof(RemoteSettings))
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
        }

        var settingsPath = Path.Combine(paths.ConfigDirectory, "settings.txt");
        if (!File.Exists(settingsPath))
        {
            return;
        }

        try
        {
            foreach (var line in File.ReadLines(settingsPath))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                {
                    continue;
                }

                var colonIndex = trimmed.IndexOf(':');
                if (colonIndex < 0)
                {
                    continue;
                }

                var key = trimmed[..colonIndex].Trim();
                var value = trimmed[(colonIndex + 1)..].Trim();

                if (key.Equals("remote_port", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(value, out var port) && port > 0)
                    {
                        Port = port;
                    }
                }
                else if (key.Equals("remote_token", StringComparison.OrdinalIgnoreCase))
                {
                    Token = value.Length > 0 ? value : null;
                }
                else if (key.Equals("remote_allowed_tabs", StringComparison.OrdinalIgnoreCase))
                {
                    var tabs = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var tab in value.Split('|'))
                    {
                        var name = tab.Trim();
                        if (name.Length > 0)
                        {
                            tabs.Add(name);
                        }
                    }

                    AllowedTabs = tabs;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
