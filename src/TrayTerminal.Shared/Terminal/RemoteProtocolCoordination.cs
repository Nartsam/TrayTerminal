namespace TrayTerminal.Shared.Terminal;

/// <summary>
/// Gates client operations at the synchronization boundary and keeps the
/// combined input/interrupt count bounded. Ordinary input deliberately leaves
/// the final slot available to the independent interrupt path.
/// </summary>
public sealed class RemoteOperationGate
{
    public const int MaxOperations = 64;

    private readonly object _lock = new();
    private int _active;
    private bool _open;

    public bool IsOpen
    {
        get
        {
            lock (_lock)
            {
                return _open;
            }
        }
    }

    public int ActiveCount
    {
        get
        {
            lock (_lock)
            {
                return _active;
            }
        }
    }

    public void Open()
    {
        lock (_lock)
        {
            _open = true;
        }
    }

    public void Close()
    {
        lock (_lock)
        {
            _open = false;
        }
    }

    public bool TryBegin(bool interrupt)
    {
        lock (_lock)
        {
            var limit = interrupt ? MaxOperations : MaxOperations - 1;
            if (!_open || _active >= limit)
            {
                return false;
            }

            _active++;
            return true;
        }
    }

    public void End()
    {
        lock (_lock)
        {
            if (_active <= 0)
            {
                throw new InvalidOperationException(
                    "Remote operation count cannot become negative.");
            }

            _active--;
        }
    }
}

/// <summary>
/// Verifies the live portion of a snapshot/replay/output stream. A terminal end
/// marker may only be queued after every sequence through its final boundary has
/// been observed exactly once.
/// </summary>
public sealed class TerminalDeliveryTracker
{
    private long _nextSequence;

    public TerminalDeliveryTracker(long initialLatestSequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialLatestSequence);
        _nextSequence = checked(initialLatestSequence + 1);
    }

    public long LastContinuousSequence => _nextSequence - 1;

    public bool TryObserve(long sequence)
    {
        if (sequence != _nextSequence)
        {
            return false;
        }

        _nextSequence = checked(_nextSequence + 1);
        return true;
    }

    public bool Reaches(long finalSequence)
    {
        return finalSequence >= 0 && LastContinuousSequence == finalSequence;
    }
}
