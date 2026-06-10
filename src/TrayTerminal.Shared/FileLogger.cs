namespace TrayTerminal.Shared;

public sealed class FileLogger
{
    private const int DefaultRetentionDays = 30;
    private static int _logRetentionDays = DefaultRetentionDays;
    private static bool _settingsLoaded;
    private static readonly object SettingsLock = new();
    private static readonly HashSet<string> CleanedDirectories = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _gate = new();
    private readonly string _name;
    private readonly string _logsDirectory;
    private string _filePath;
    private string _currentDate;

    /// <summary>
    /// Log retention period in days. Configurable via settings.txt (log_retention_days).
    /// </summary>
    public static int LogRetentionDays
    {
        get => Volatile.Read(ref _logRetentionDays);
        set => Volatile.Write(ref _logRetentionDays, value);
    }

    public FileLogger(PortablePaths paths, string name)
    {
        paths.EnsureCreated();
        _name = name;
        _logsDirectory = paths.LogsDirectory;
        _currentDate = DateTimeOffset.Now.ToString("yyyyMMdd");
        _filePath = Path.Combine(_logsDirectory, $"{_name}-{_currentDate}.log");
        CleanOldLogs();
    }

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(Exception exception, string message)
    {
        Write("ERROR", $"{message}{Environment.NewLine}{exception}");
    }

    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var now = DateTimeOffset.Now;
        var today = now.ToString("yyyyMMdd");

        // When crossing midnight, rotate to a new log file and clean up old logs.
        if (today != _currentDate)
        {
            lock (_gate)
            {
                if (today != _currentDate)
                {
                    _currentDate = today;
                    _filePath = Path.Combine(_logsDirectory, $"{_name}-{today}.log");
                    CleanOldLogs();
                }
            }
        }

        var line = $"{now:O} [{level}] {message}{Environment.NewLine}";
        lock (_gate)
        {
            File.AppendAllText(_filePath, line);
        }
    }

    private void CleanOldLogs()
    {
        // Run at most once per (directory, date) pair per process so that
        // midnights that generate many log lines don't trigger repeated scans.
        var key = $"{_logsDirectory}|{_currentDate}";
        lock (CleanedDirectories)
        {
            if (!CleanedDirectories.Add(key))
            {
                return;
            }
        }

        try
        {
            var cutoff = DateTimeOffset.Now.AddDays(-LogRetentionDays);
            foreach (var file in Directory.EnumerateFiles(_logsDirectory, "*.log"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch (IOException)
                {
                    // File in use or permission denied — skip and continue.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Reads <c>Data\Config\settings.txt</c> and applies recognised settings.
    /// Safe to call multiple times — only the first call has any effect.
    /// </summary>
    public static void LoadSettings(PortablePaths paths)
    {
        lock (SettingsLock)
        {
            if (_settingsLoaded)
            {
                return;
            }

            _settingsLoaded = true;
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

                if (key.Equals("log_retention_days", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(value, out var days) && days > 0)
                    {
                        LogRetentionDays = days;
                    }
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
