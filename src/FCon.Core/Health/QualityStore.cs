using FCon.Abstractions.Model;
using FCon.Core.Storage;

namespace FCon.Core.Health;

public sealed class QualityState
{
    public Dictionary<Guid, NodeQuality> Nodes { get; set; } = [];
}

/// <summary>
/// Persists what has been learned about each server. Kept separate from the profile list
/// so that re-importing a subscription does not throw away a working server's history.
/// </summary>
public sealed class QualityStore
{
    private readonly JsonStore<QualityState> _store;
    private readonly Lock _gate = new();
    private readonly QualityState _state;
    private Task _pendingWrite = Task.CompletedTask;

    public QualityStore(string? path = null)
    {
        _store = new JsonStore<QualityState>(
            path ?? Path.Combine(AppPaths.DataDirectory, "quality.json"),
            () => new QualityState());
        _state = _store.Load();
    }

    public event Action<Guid>? Updated;

    public NodeQuality Get(Guid nodeId)
    {
        lock (_gate)
        {
            return _state.Nodes.TryGetValue(nodeId, out var q)
                ? q
                : new NodeQuality { NodeId = nodeId };
        }
    }

    public void RecordLatency(Guid nodeId, int milliseconds) =>
        Mutate(nodeId, q => q.WithLatency(milliseconds));

    public void RecordSuccess(Guid nodeId) =>
        Mutate(nodeId, q => q.WithSuccess(DateTimeOffset.Now));

    public void RecordFailure(Guid nodeId, string? error) =>
        Mutate(nodeId, q => q.WithFailure(DateTimeOffset.Now, error));

    /// <summary>Clear a server's history — used when the user edits it into something else.</summary>
    public void Forget(Guid nodeId)
    {
        lock (_gate) _state.Nodes.Remove(nodeId);
        Commit(nodeId);
    }

    /// <summary>
    /// Rank candidates best-first. Quarantined servers sort last but are still returned,
    /// so a failover has something to try when every option has been failing.
    /// </summary>
    public IReadOnlyList<ProxyNode> Rank(IEnumerable<ProxyNode> candidates)
    {
        var now = DateTimeOffset.Now;
        return
        [
            .. candidates
                .Select(node => (Node: node, Quality: Get(node.Id)))
                .OrderByDescending(x => x.Quality.Score(now))
                .ThenBy(x => x.Quality.LatencyMs ?? double.MaxValue)
                .Select(x => x.Node)
        ];
    }

    public Task FlushAsync()
    {
        lock (_gate) return _pendingWrite;
    }

    private void Mutate(Guid nodeId, Func<NodeQuality, NodeQuality> update)
    {
        lock (_gate)
        {
            var current = _state.Nodes.TryGetValue(nodeId, out var q)
                ? q
                : new NodeQuality { NodeId = nodeId };
            _state.Nodes[nodeId] = update(current) with { NodeId = nodeId };
        }
        Commit(nodeId);
    }

    private void Commit(Guid nodeId)
    {
        QualityState snapshot;
        lock (_gate)
        {
            snapshot = new QualityState { Nodes = new Dictionary<Guid, NodeQuality>(_state.Nodes) };

            // Serialised writes, same as the profile store: the last state wins and
            // shutdown can wait for the queue to drain.
            _pendingWrite = _pendingWrite.ContinueWith(
                _ => _store.SaveAsync(snapshot),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }
        Updated?.Invoke(nodeId);
    }
}
