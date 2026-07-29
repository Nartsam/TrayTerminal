namespace TrayTerminal.Shared;

/// <summary>
/// A non-blocking bounded concurrency gate. Rejected callers never queue, so an
/// attacker cannot replace a socket backlog with an unbounded waiter backlog.
/// </summary>
public sealed class ConcurrencySlotLimiter
{
    private readonly int _capacity;
    private int _active;

    public ConcurrencySlotLimiter(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    public int ActiveCount => Volatile.Read(ref _active);

    public bool TryAcquire(out IDisposable? lease)
    {
        while (true)
        {
            var current = Volatile.Read(ref _active);
            if (current >= _capacity)
            {
                lease = null;
                return false;
            }

            if (Interlocked.CompareExchange(
                    ref _active,
                    current + 1,
                    current) == current)
            {
                lease = new Lease(this);
                return true;
            }
        }
    }

    private void Release()
    {
        Interlocked.Decrement(ref _active);
    }

    private sealed class Lease : IDisposable
    {
        private ConcurrencySlotLimiter? _owner;

        public Lease(ConcurrencySlotLimiter owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release();
        }
    }
}
