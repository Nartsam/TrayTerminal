namespace TrayTerminal.Shared.Ipc;

/// <summary>
/// Correlates Host acknowledgements with bounded-lifetime App input requests.
/// It never stores input payloads, only request IDs and completion sources.
/// </summary>
public sealed class InputAckRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<long, TaskCompletionSource<bool>> _pending = new();
    private bool _failed;

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count;
            }
        }
    }

    public bool TryRegister(long requestId, out Task<bool> completion)
    {
        var source = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            if (!_failed && _pending.TryAdd(requestId, source))
            {
                completion = source.Task;
                return true;
            }
        }

        completion = Task.FromResult(false);
        return false;
    }

    public bool TryAcknowledge(long requestId)
    {
        TaskCompletionSource<bool>? source;
        lock (_lock)
        {
            _pending.TryGetValue(requestId, out source);
        }

        return source is not null && source.TrySetResult(true);
    }

    public void Remove(long requestId)
    {
        lock (_lock)
        {
            _pending.Remove(requestId);
        }
    }

    public void FailAll()
    {
        TaskCompletionSource<bool>[] pending;
        lock (_lock)
        {
            if (_failed)
            {
                return;
            }

            _failed = true;
            pending = _pending.Values.ToArray();
            _pending.Clear();
        }

        foreach (var source in pending)
        {
            source.TrySetResult(false);
        }
    }
}
