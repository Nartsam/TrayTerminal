using System.Threading.Channels;

namespace TrayTerminal.Shared.Terminal;

public sealed record TerminalOutput(long Sequence, byte[] Data);

public sealed record TerminalSnapshot(
    long Sequence,
    byte[] Data,
    TerminalSize Size);

public sealed record TerminalInitialState(
    TerminalSnapshot Snapshot,
    IReadOnlyList<TerminalOutput> Replay,
    long LatestSequence);

/// <summary>
/// Keeps a bounded checkpoint and the output after it. The visible xterm instance
/// produces checkpoints; this class makes snapshot + replay subscription atomic.
/// </summary>
public sealed class TerminalStateHub : IDisposable
{
    public const int DefaultTailByteLimit = 8 * 1024 * 1024;
    public const int DefaultTailItemLimit = 4096;
    public const int DefaultSnapshotByteLimit = 8 * 1024 * 1024;
    public const int DefaultSubscriberQueueCapacity = 512;
    public const int DefaultMaxSubscribers = 8;

    private readonly object _lock = new();
    private readonly int _tailByteLimit;
    private readonly int _tailItemLimit;
    private readonly int _snapshotByteLimit;
    private readonly int _subscriberQueueCapacity;
    private readonly int _maxSubscribers;
    private readonly LinkedList<TerminalOutput> _tail = new();
    private readonly Dictionary<Guid, Channel<TerminalOutput>> _subscribers = new();
    private TerminalSnapshot? _snapshot;
    private long _latestSequence;
    private int _tailBytes;
    private bool _disposed;

    public TerminalStateHub(
        int tailByteLimit = DefaultTailByteLimit,
        int tailItemLimit = DefaultTailItemLimit,
        int snapshotByteLimit = DefaultSnapshotByteLimit,
        int subscriberQueueCapacity = DefaultSubscriberQueueCapacity,
        int maxSubscribers = DefaultMaxSubscribers,
        TerminalSize? initialSize = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tailByteLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(snapshotByteLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(subscriberQueueCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSubscribers);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tailItemLimit);
        _tailByteLimit = tailByteLimit;
        _tailItemLimit = tailItemLimit;
        _snapshotByteLimit = snapshotByteLimit;
        _subscriberQueueCapacity = subscriberQueueCapacity;
        _maxSubscribers = maxSubscribers;
        _snapshot = new TerminalSnapshot(
            0,
            [],
            initialSize ?? new TerminalSize(120, 30));
    }

    public long LatestSequence
    {
        get
        {
            lock (_lock)
            {
                return _latestSequence;
            }
        }
    }

    public bool HasRecoverableState
    {
        get
        {
            lock (_lock)
            {
                return _snapshot is not null;
            }
        }
    }

    public int SubscriberCount
    {
        get
        {
            lock (_lock)
            {
                return _subscribers.Count;
            }
        }
    }

    public TerminalOutput Publish(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        List<KeyValuePair<Guid, Channel<TerminalOutput>>>? slowSubscribers = null;
        TerminalOutput output;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            output = new TerminalOutput(++_latestSequence, data);
            _tail.AddLast(output);
            _tailBytes += data.Length;

            // Keep a rolling bounded tail even after invalidation. A later
            // checkpoint can restore validity if it covers everything trimmed.
            // A byte-only limit is insufficient for a process that emits many
            // tiny writes: node and array overhead could otherwise dwarf the VT
            // payload while remaining under the advertised byte ceiling.
            while ((_tailBytes > _tailByteLimit || _tail.Count > _tailItemLimit)
                && _tail.First is { } first)
            {
                _tail.RemoveFirst();
                _tailBytes -= first.Value.Data.Length;
                if (_snapshot is not null && first.Value.Sequence > _snapshot.Sequence)
                {
                    _snapshot = null;
                }
            }

            foreach (var subscriber in _subscribers)
            {
                if (!subscriber.Value.Writer.TryWrite(output))
                {
                    slowSubscribers ??= [];
                    slowSubscribers.Add(subscriber);
                }
            }

            if (slowSubscribers is not null)
            {
                foreach (var slowSubscriber in slowSubscribers)
                {
                    _subscribers.Remove(slowSubscriber.Key);
                    slowSubscriber.Value.Writer.TryComplete(
                        new TerminalSubscriberTooSlowException());
                }
            }
        }

        return output;
    }

    public bool TryAcceptSnapshot(
        long sequence,
        byte[] data,
        TerminalSize size)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length > _snapshotByteLimit)
        {
            return false;
        }

        lock (_lock)
        {
            if (_disposed || sequence < 0 || sequence > _latestSequence)
            {
                return false;
            }

            if (_snapshot is not null && sequence < _snapshot.Sequence)
            {
                return false;
            }

            // If old output was already trimmed, only a checkpoint at or after
            // the missing boundary can make the stream recoverable again.
            var firstTailSequence = _tail.First?.Value.Sequence ?? (_latestSequence + 1);
            if (sequence < firstTailSequence - 1)
            {
                return false;
            }

            _snapshot = new TerminalSnapshot(sequence, data, size);
            while (_tail.First is { } first && first.Value.Sequence <= sequence)
            {
                _tail.RemoveFirst();
                _tailBytes -= first.Value.Data.Length;
            }

            return true;
        }
    }

    public void InvalidateCheckpoint()
    {
        lock (_lock)
        {
            if (!_disposed)
            {
                // Existing subscribers already have a complete state and continue
                // live. New/recovering renderers wait for a checkpoint that
                // includes the external state transition (for example resize).
                _snapshot = null;
            }
        }
    }

    public bool TryGetRecoveryState(out TerminalInitialState? state)
    {
        lock (_lock)
        {
            if (_disposed || _snapshot is null)
            {
                state = null;
                return false;
            }

            state = new TerminalInitialState(
                _snapshot,
                _tail.ToArray(),
                _latestSequence);
            return true;
        }
    }

    public bool TrySubscribe(out TerminalStateSubscription? subscription)
    {
        lock (_lock)
        {
            if (_disposed
                || _snapshot is null
                || _subscribers.Count >= _maxSubscribers)
            {
                subscription = null;
                return false;
            }

            var id = Guid.NewGuid();
            var channel = Channel.CreateBounded<TerminalOutput>(
                new BoundedChannelOptions(_subscriberQueueCapacity)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    // Publish uses TryWrite. A full queue removes the subscriber
                    // atomically instead of dropping terminal state.
                    FullMode = BoundedChannelFullMode.Wait
                });

            var initialState = new TerminalInitialState(
                _snapshot,
                _tail.ToArray(),
                _latestSequence);
            _subscribers.Add(id, channel);
            subscription = new TerminalStateSubscription(
                initialState,
                channel.Reader,
                () => RemoveSubscriber(id, channel));
            return true;
        }
    }

    private void RemoveSubscriber(Guid id, Channel<TerminalOutput> channel)
    {
        lock (_lock)
        {
            if (_subscribers.Remove(id))
            {
                channel.Writer.TryComplete();
            }
        }
    }

    public void Dispose()
    {
        Channel<TerminalOutput>[] subscribers;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            subscribers = _subscribers.Values.ToArray();
            _subscribers.Clear();
            _tail.Clear();
            _tailBytes = 0;
            _snapshot = null;
        }

        foreach (var subscriber in subscribers)
        {
            subscriber.Writer.TryComplete();
        }
    }
}

public sealed class TerminalStateSubscription : IDisposable
{
    private Action? _dispose;

    internal TerminalStateSubscription(
        TerminalInitialState initialState,
        ChannelReader<TerminalOutput> outputs,
        Action dispose)
    {
        InitialState = initialState;
        Outputs = outputs;
        _dispose = dispose;
    }

    public TerminalInitialState InitialState { get; }

    public ChannelReader<TerminalOutput> Outputs { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

public sealed class TerminalSubscriberTooSlowException : Exception
{
    public TerminalSubscriberTooSlowException()
        : base("The terminal output subscriber could not keep up.")
    {
    }
}
