namespace TrayTerminal.Shared;

public sealed class FileLogger
{
    private readonly object _gate = new();
    private readonly string _filePath;

    public FileLogger(PortablePaths paths, string name)
    {
        paths.EnsureCreated();
        _filePath = Path.Combine(paths.LogsDirectory, $"{name}-{DateTimeOffset.Now:yyyyMMdd}.log");
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
        var line = $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}";
        lock (_gate)
        {
            File.AppendAllText(_filePath, line);
        }
    }
}
