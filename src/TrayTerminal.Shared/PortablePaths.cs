using System.Diagnostics.CodeAnalysis;

namespace TrayTerminal.Shared;

public sealed class PortablePaths
{
    private readonly string _baseDirectory;

    public PortablePaths(string? baseDirectory = null)
    {
        _baseDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        BaseDirectory = EnsureTrailingSeparator(_baseDirectory);
        DataDirectory = CombineInsideBase("Data");
        ConfigDirectory = CombineInsideBase("Data", "Config");
        LogsDirectory = CombineInsideBase("Data", "Logs");
        TempDirectory = CombineInsideBase("Data", "Temp");
        WebView2DataDirectory = CombineInsideBase("Data", "WebView2");
        BackgroundsDirectory = CombineInsideBase("Data", "Backgrounds");
    }

    public string BaseDirectory { get; }

    public string DataDirectory { get; }

    public string ConfigDirectory { get; }

    public string LogsDirectory { get; }

    public string TempDirectory { get; }

    public string WebView2DataDirectory { get; }

    public string BackgroundsDirectory { get; }

    public string HostExecutablePath => CombineInsideBase("TrayTerminal.Host.exe");

    public string TerminalHtmlPath => CombineInsideBase("Assets", "terminal.html");

    public string AppIconPath => CombineInsideBase("Assets", "app.ico");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(TempDirectory);
        Directory.CreateDirectory(WebView2DataDirectory);
        Directory.CreateDirectory(BackgroundsDirectory);
    }

    public string CombineInsideBase(params string[] parts)
    {
        // Keep all application-owned state inside the portable executable directory.
        // This is the central guard used by App, Host, tests, and background lookup.
        var combined = Path.GetFullPath(Path.Combine([BaseDirectory, .. parts]));
        if (!IsInsideBase(combined))
        {
            throw new InvalidOperationException($"Path escapes the portable base directory: {combined}");
        }

        return combined;
    }

    public bool IsInsideBase([NotNullWhen(true)] string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(BaseDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }
}
