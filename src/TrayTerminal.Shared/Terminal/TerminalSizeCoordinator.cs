namespace TrayTerminal.Shared.Terminal;

public enum TerminalClientKind
{
    Local,
    Remote
}

public readonly record struct TerminalSize(int Columns, int Rows)
{
    public static TerminalSize Clamp(int columns, int rows)
    {
        return new TerminalSize(
            Math.Clamp(columns, 20, 500),
            Math.Clamp(rows, 5, 200));
    }
}

public readonly record struct TerminalSizeUpdate(
    TerminalSize Size,
    Guid? OwnerId,
    bool SizeChanged,
    bool OwnerChanged,
    long Revision)
{
    public bool ShouldBroadcast => SizeChanged || OwnerChanged;
}

/// <summary>
/// Selects exactly one ConPTY size owner. A visible local renderer wins; while
/// the app is hidden, the earliest connected remote client owns the lease.
/// </summary>
public sealed class TerminalSizeCoordinator
{
    private readonly Dictionary<Guid, ClientState> _clients = new();
    private long _nextOrder;
    private TerminalSize _size;
    private Guid? _ownerId;
    private long _revision;

    public TerminalSizeCoordinator(int columns = 120, int rows = 30)
    {
        _size = TerminalSize.Clamp(columns, rows);
    }

    public TerminalSize CurrentSize => _size;

    public Guid? OwnerId => _ownerId;

    public long Revision => _revision;

    public TerminalSizeUpdate Register(
        Guid clientId,
        TerminalClientKind kind,
        bool isVisible,
        int columns,
        int rows)
    {
        _clients[clientId] = new ClientState(
            kind,
            isVisible,
            TerminalSize.Clamp(columns, rows),
            ++_nextOrder);
        return RecalculateOwner();
    }

    public TerminalSizeUpdate SetLocalVisibility(
        Guid clientId,
        bool isVisible,
        int columns,
        int rows)
    {
        if (_clients.TryGetValue(clientId, out var client)
            && client.Kind == TerminalClientKind.Local)
        {
            _clients[clientId] = client with
            {
                IsVisible = isVisible,
                RequestedSize = TerminalSize.Clamp(columns, rows)
            };
        }

        return RecalculateOwner();
    }

    public TerminalSizeUpdate RequestResize(Guid clientId, int columns, int rows)
    {
        if (!_clients.TryGetValue(clientId, out var client))
        {
            return new TerminalSizeUpdate(_size, _ownerId, false, false, _revision);
        }

        var requestedSize = TerminalSize.Clamp(columns, rows);
        _clients[clientId] = client with { RequestedSize = requestedSize };
        if (_ownerId != clientId)
        {
            return new TerminalSizeUpdate(_size, _ownerId, false, false, _revision);
        }

        var changed = requestedSize != _size;
        _size = requestedSize;
        return new TerminalSizeUpdate(
            _size,
            _ownerId,
            changed,
            false,
            changed ? ++_revision : _revision);
    }

    public TerminalSizeUpdate Unregister(Guid clientId)
    {
        _clients.Remove(clientId);
        return RecalculateOwner();
    }

    private TerminalSizeUpdate RecalculateOwner()
    {
        var previousOwner = _ownerId;
        var nextOwner = _clients
            .Where(pair => pair.Value.Kind == TerminalClientKind.Local && pair.Value.IsVisible)
            .OrderBy(pair => pair.Value.Order)
            .Select(pair => (Guid?)pair.Key)
            .FirstOrDefault()
            ?? _clients
                .Where(pair => pair.Value.Kind == TerminalClientKind.Remote)
                .OrderBy(pair => pair.Value.Order)
                .Select(pair => (Guid?)pair.Key)
                .FirstOrDefault();

        _ownerId = nextOwner;
        var ownerChanged = previousOwner != nextOwner;
        var sizeChanged = false;
        if (nextOwner is { } owner && _clients.TryGetValue(owner, out var state))
        {
            sizeChanged = state.RequestedSize != _size;
            _size = state.RequestedSize;
        }

        return new TerminalSizeUpdate(
            _size,
            _ownerId,
            sizeChanged,
            ownerChanged,
            sizeChanged || ownerChanged ? ++_revision : _revision);
    }

    private sealed record ClientState(
        TerminalClientKind Kind,
        bool IsVisible,
        TerminalSize RequestedSize,
        long Order);
}
