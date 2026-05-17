using TrayTerminal.Shared.Terminal;

namespace TrayTerminal.App.Dialogs;

public sealed record NewTerminalRequest(string Title, TerminalProfile Profile, bool RunAsAdministrator);
