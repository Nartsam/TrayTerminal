using Microsoft.Web.WebView2.Core;
using TrayTerminal.Shared;

namespace TrayTerminal.App.Services;

/// <summary>
/// Owns the process-wide WebView2 environment. WebView2 controls that use the
/// same user-data folder also share one browser process, so keeping one cached
/// environment makes that lifetime explicit and gives failures one invalidation
/// point.
/// </summary>
public sealed class WebView2EnvironmentManager
{
    private const int RpcEDisconnected = unchecked((int)0x80010108);
    private readonly object _gate = new();
    private readonly string _userDataFolder;
    private readonly FileLogger _logger;
    private Task<CoreWebView2Environment>? _environmentTask;

    public WebView2EnvironmentManager(PortablePaths paths, FileLogger logger)
    {
        _userDataFolder = paths.WebView2DataDirectory;
        _logger = logger;
    }

    public async Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        Task<CoreWebView2Environment> environmentTask;
        lock (_gate)
        {
            environmentTask = _environmentTask ??= CreateEnvironmentAsync();
        }

        try
        {
            return await environmentTask;
        }
        catch
        {
            // A faulted creation task must not poison all future terminal tabs.
            lock (_gate)
            {
                if (ReferenceEquals(_environmentTask, environmentTask))
                {
                    _environmentTask = null;
                }
            }

            throw;
        }
    }

    public void Invalidate(CoreWebView2Environment environment, string reason)
    {
        var invalidated = false;
        lock (_gate)
        {
            if (_environmentTask is { IsCompletedSuccessfully: true } environmentTask
                && ReferenceEquals(environmentTask.Result, environment))
            {
                _environmentTask = null;
                invalidated = true;
            }
        }

        if (!invalidated)
        {
            return;
        }

        try
        {
            environment.BrowserProcessExited -= OnBrowserProcessExited;
        }
        catch (Exception exception) when (exception.HResult == RpcEDisconnected)
        {
            // The environment's browser process is already gone, so there is no
            // live event source left to unsubscribe from.
        }

        _logger.Warn($"WebView2 environment invalidated: {reason}");
    }

    private async Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        var environment = await CoreWebView2Environment.CreateAsync(null, _userDataFolder);
        environment.BrowserProcessExited += OnBrowserProcessExited;
        _logger.Info(
            $"Created WebView2 environment {environment.BrowserVersionString} "
            + $"with user data folder '{environment.UserDataFolder}'.");
        return environment;
    }

    private void OnBrowserProcessExited(
        object? sender,
        CoreWebView2BrowserProcessExitedEventArgs args)
    {
        if (sender is not CoreWebView2Environment environment)
        {
            return;
        }

        Invalidate(
            environment,
            $"browser process {args.BrowserProcessId} exited ({args.BrowserProcessExitKind})");
    }
}
