using System.Text;

namespace TrayTerminal.Host.Terminal;

internal static class ShellIntegration
{
    public static ShellLaunchConfiguration Prepare(
        string profileId,
        string arguments,
        string markerToken)
    {
        return profileId switch
        {
            "cmd" => new ShellLaunchConfiguration(
                arguments,
                BuildCommandPrompt(markerToken),
                PromptTrackingEnabled: true),
            "windows-powershell" or "pwsh" => new ShellLaunchConfiguration(
                BuildPowerShellArguments(arguments, markerToken),
                CommandPrompt: null,
                PromptTrackingEnabled: true),
            _ => new ShellLaunchConfiguration(
                arguments,
                CommandPrompt: null,
                PromptTrackingEnabled: false)
        };
    }

    private static string BuildCommandPrompt(string markerToken)
    {
        // Set PROMPT only in the child environment block. Nested CMD instances
        // inherit it, but Job Object evidence keeps them classified as active.
        var existing = Environment.GetEnvironmentVariable("PROMPT");
        if (string.IsNullOrEmpty(existing))
        {
            existing = "$P$G";
        }

        // $P reports the current drive and path for "open containing folder".
        return existing
            + "$E]633;TrayTerminal;"
            + markerToken
            + ";0;$P$E\\";
    }

    private static string BuildPowerShellArguments(
        string arguments,
        string markerToken)
    {
        var script = $$"""
            $global:__TrayTerminalOriginalPrompt = ${function:prompt}
            function global:prompt {
                $trayTerminalPromptResult = & $global:__TrayTerminalOriginalPrompt
                $trayTerminalActiveJobs = @(
                    Get-Job -ErrorAction SilentlyContinue |
                        Where-Object { $_.State -notin @('Completed', 'Failed', 'Stopped') }
                ).Count
                # Set-Location does not change the process directory, so report
                # the last file-system location; it survives "cd HKLM:" as well.
                $trayTerminalLocation = ''
                try {
                    $trayTerminalLocation =
                        $executionContext.SessionState.Path.CurrentFileSystemLocation.ProviderPath
                } catch {}
                [Console]::Write(
                    [char]27 + ']633;TrayTerminal;{{markerToken}};' +
                    $trayTerminalActiveJobs + ';' + $trayTerminalLocation +
                    [char]27 + '\')
                $trayTerminalPromptResult
            }
            """;
        var encoded = Convert.ToBase64String(
            Encoding.Unicode.GetBytes(script));
        return string.IsNullOrWhiteSpace(arguments)
            ? $"-NoExit -EncodedCommand {encoded}"
            : $"{arguments} -NoExit -EncodedCommand {encoded}";
    }
}

internal sealed record ShellLaunchConfiguration(
    string Arguments,
    string? CommandPrompt,
    bool PromptTrackingEnabled);
