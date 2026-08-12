namespace TrayTerminal.Shared.Terminal;

public sealed class TerminalActivityTracker
{
    private readonly object _sync = new();
    private bool _promptObserved;
    private bool _hasUnresolvedInput;
    private bool _lastInputWasCarriageReturn;
    private long _pendingSubmissions;
    private long _ignoredFocusReports;
    private int _activeShellJobs;

    public void RecordInput(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        lock (_sync)
        {
            // xterm emits these VT focus reports through the same onData channel
            // as keyboard input when DECSET 1004 is enabled. Opening a WPF close
            // dialog blurs the terminal and produces ESC[O; it is terminal
            // protocol traffic, not an unfinished command line.
            if (IsTerminalFocusReport(data))
            {
                if (_ignoredFocusReports < long.MaxValue)
                {
                    _ignoredFocusReports++;
                }
                return;
            }

            foreach (var value in data)
            {
                if (value == (byte)'\n' && _lastInputWasCarriageReturn)
                {
                    _lastInputWasCarriageReturn = false;
                    continue;
                }

                _lastInputWasCarriageReturn = false;
                if (value is (byte)'\r' or (byte)'\n')
                {
                    IncrementPendingSubmissions();
                    _hasUnresolvedInput = false;
                    _lastInputWasCarriageReturn = value == (byte)'\r';
                    continue;
                }

                if (value == 0x03)
                {
                    // Ctrl+C completes the current submitted command, or turns a
                    // partially entered line into one prompt-producing action.
                    if (_pendingSubmissions == 0)
                    {
                        IncrementPendingSubmissions();
                    }
                    _hasUnresolvedInput = false;
                    continue;
                }

                _hasUnresolvedInput = true;
            }
        }
    }

    public void RecordPrompt(int activeShellJobs)
    {
        lock (_sync)
        {
            // The first marker establishes the root-shell baseline. Input may
            // already be queued while the startup prompt is still in ConPTY's
            // output buffer, so it must never complete a submitted command.
            if (_promptObserved && _pendingSubmissions > 0)
            {
                _pendingSubmissions--;
            }

            _promptObserved = true;
            _activeShellJobs = Math.Max(0, activeShellJobs);
        }
    }

    public TerminalShellActivitySnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return new TerminalShellActivitySnapshot(
                _promptObserved,
                _pendingSubmissions,
                _hasUnresolvedInput,
                _activeShellJobs,
                _ignoredFocusReports);
        }
    }

    private static bool IsTerminalFocusReport(ReadOnlySpan<byte> data)
    {
        return data.Length == 3
            && data[0] == 0x1b
            && data[1] == (byte)'['
            && data[2] is (byte)'I' or (byte)'O';
    }

    private void IncrementPendingSubmissions()
    {
        if (_pendingSubmissions < long.MaxValue)
        {
            _pendingSubmissions++;
        }
    }
}

public readonly record struct TerminalShellActivitySnapshot(
    bool PromptObserved,
    long PendingSubmissions,
    bool HasUnresolvedInput,
    int ActiveShellJobs,
    long IgnoredFocusReports);
