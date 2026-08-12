using TrayTerminal.Shared.Terminal;

namespace TrayTerminal.App.Services;

public sealed class TerminalCloseCheckLease : IAsyncDisposable
{
    private readonly Func<bool, ValueTask>? _release;
    private int _committed;
    private int _disposed;

    internal TerminalCloseCheckLease(
        TerminalCloseAssessment assessment,
        Func<bool, ValueTask>? release = null)
    {
        Assessment = assessment;
        _release = release;
    }

    public TerminalCloseAssessment Assessment { get; }

    public void Commit()
    {
        Interlocked.Exchange(ref _committed, 1);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0
            || _release is null)
        {
            return;
        }

        await _release(Volatile.Read(ref _committed) == 0);
    }
}
