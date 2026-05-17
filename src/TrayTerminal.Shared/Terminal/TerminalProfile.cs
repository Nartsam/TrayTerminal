namespace TrayTerminal.Shared.Terminal;

public sealed record TerminalProfile(
    string Id,
    string DisplayName,
    string ExecutablePath,
    string Arguments,
    string WorkingDirectory)
{
    public override string ToString() => DisplayName;
}
