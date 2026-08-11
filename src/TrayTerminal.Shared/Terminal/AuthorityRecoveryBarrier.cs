namespace TrayTerminal.Shared.Terminal;

public enum AuthorityAvailability
{
    Recovering,
    Ready,
    Failed
}

/// <summary>
/// Publishes a generation-scoped readiness barrier. A renderer may exist while
/// recovery is in progress, but ordinary producers cannot cross the barrier
/// until that exact generation is committed ready.
/// </summary>
public sealed class AuthorityRecoveryBarrier
{
    private readonly object _lock = new();
    private TaskCompletionSource _ready = NewSource();
    private long _generation;
    private AuthorityAvailability _availability = AuthorityAvailability.Recovering;

    public long Generation
    {
        get
        {
            lock (_lock)
            {
                return _generation;
            }
        }
    }

    public AuthorityAvailability Availability
    {
        get
        {
            lock (_lock)
            {
                return _availability;
            }
        }
    }

    public (long Generation, bool Started) BeginRecovery()
    {
        lock (_lock)
        {
            if (_availability == AuthorityAvailability.Recovering)
            {
                return (_generation, false);
            }

            _generation++;
            _availability = AuthorityAvailability.Recovering;
            _ready = NewSource();
            return (_generation, true);
        }
    }

    public long RestartRecovery()
    {
        lock (_lock)
        {
            _generation++;
            _availability = AuthorityAvailability.Recovering;
            _ready.TrySetException(new AuthorityGenerationSupersededException());
            _ = _ready.Task.Exception;
            _ready = NewSource();
            return _generation;
        }
    }

    public void StartInitialRecovery()
    {
        lock (_lock)
        {
            _generation = 1;
            _availability = AuthorityAvailability.Recovering;
            _ready = NewSource();
        }
    }

    public bool TrySetReady(long generation)
    {
        lock (_lock)
        {
            if (generation != _generation
                || _availability != AuthorityAvailability.Recovering)
            {
                return false;
            }

            _availability = AuthorityAvailability.Ready;
            _ready.TrySetResult();
            return true;
        }
    }

    public bool TrySetFailed(long generation, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_lock)
        {
            if (generation != _generation)
            {
                return false;
            }

            _availability = AuthorityAvailability.Failed;
            _ready.TrySetException(exception);
            _ = _ready.Task.Exception;
            return true;
        }
    }

    public Task WaitReadyAsync(CancellationToken cancellationToken = default)
    {
        Task task;
        lock (_lock)
        {
            task = _ready.Task;
        }

        return task.WaitAsync(cancellationToken);
    }

    public bool IsCurrentReady(long generation)
    {
        lock (_lock)
        {
            return generation == _generation
                && _availability == AuthorityAvailability.Ready;
        }
    }

    private static TaskCompletionSource NewSource()
    {
        return new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed class AuthorityGenerationSupersededException : Exception
{
    public AuthorityGenerationSupersededException()
        : base("The authority renderer generation was superseded.")
    {
    }
}
