namespace TrayTerminal.Shared.Terminal;

public enum TerminalCloseStatus : byte
{
    Idle = 0,
    ActiveCommand = 1,
    ActiveProcesses = 2,
    ActiveShellJobs = 3,
    UnknownInput = 4,
    PromptUnavailable = 5,
    ProcessTrackingUnavailable = 6,
    SessionUnavailable = 7,
    CloseCheckTimedOut = 8
}

public readonly record struct TerminalCloseAssessment(
    TerminalCloseStatus Status,
    int Detail = 0)
{
    public bool IsIdle => Status == TerminalCloseStatus.Idle;

    public bool HasActiveTask => Status is
        TerminalCloseStatus.ActiveCommand
        or TerminalCloseStatus.ActiveProcesses
        or TerminalCloseStatus.ActiveShellJobs;
}
