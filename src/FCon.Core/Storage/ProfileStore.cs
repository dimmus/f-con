using FCon.Abstractions.Model;

namespace FCon.Core.Storage;

/// <summary>
/// In-memory owner of the server list and subscriptions, backed by an atomic JSON file.
/// All mutations funnel through here so persistence and change notification stay in one place.
/// </summary>
public sealed class ProfileStore
{
    private readonly JsonStore<ProfileState> _store;
    private readonly Lock _gate = new();
    private ProfileState _state;

    /// <summary>
    /// Tail of the write chain. Saves stay off the caller's thread, but are strictly
    /// serialised so the last mutation is the one that ends up on disk, and
    /// <see cref="FlushAsync"/> can wait for the queue to drain before shutdown.
    /// </summary>
    private Task _pendingWrite = Task.CompletedTask;

    public ProfileStore(string? path = null)
    {
        _store = new JsonStore<ProfileState>(path ?? AppPaths.ProfilesFile, () => new ProfileState());
        _state = _store.Load();
    }

    /// <summary>Raised after any mutation, once the in-memory state is consistent.</summary>
    public event Action? Changed;

    public IReadOnlyList<ProxyNode> Nodes
    {
        get { lock (_gate) return [.. _state.Nodes]; }
    }

    public IReadOnlyList<Subscription> Subscriptions
    {
        get { lock (_gate) return [.. _state.Subscriptions]; }
    }

    /// <summary>
    /// The servers actually in play: everything except those from a deactivated
    /// subscription. Hand-entered nodes have no subscription and are always included.
    /// </summary>
    /// <remarks>
    /// Deactivating keeps the nodes on disk rather than deleting them. Re-enabling is
    /// then instant and, more importantly, the measured quality history survives - that
    /// record is earned slowly, over many probes, and is the thing that makes
    /// best-server selection work at all.
    /// </remarks>
    public IReadOnlyList<ProxyNode> ActiveNodes
    {
        get
        {
            lock (_gate)
            {
                var off = _state.Subscriptions.Where(s => !s.Enabled).Select(s => s.Id).ToHashSet();
                if (off.Count == 0) return [.. _state.Nodes];

                return [.. _state.Nodes.Where(n => n.SubscriptionId is not { } id || !off.Contains(id))];
            }
        }
    }

    /// <summary>Turn a subscription's servers on or off. Returns how many are affected.</summary>
    public int SetSubscriptionEnabled(Guid id, bool enabled)
    {
        int affected;
        lock (_gate)
        {
            var subscription = _state.Subscriptions.FirstOrDefault(s => s.Id == id);
            if (subscription is null || subscription.Enabled == enabled) return 0;

            subscription.Enabled = enabled;
            affected = _state.Nodes.Count(n => n.SubscriptionId == id);
        }
        Commit();
        return affected;
    }

    public ProxyNode? FindNode(Guid id)
    {
        lock (_gate) return _state.Nodes.FirstOrDefault(n => n.Id == id);
    }

    public void AddNodes(IEnumerable<ProxyNode> nodes)
    {
        lock (_gate) _state.Nodes.AddRange(nodes);
        Commit();
    }

    public void UpsertNode(ProxyNode node)
    {
        lock (_gate)
        {
            var index = _state.Nodes.FindIndex(n => n.Id == node.Id);
            if (index >= 0) _state.Nodes[index] = node;
            else _state.Nodes.Add(node);
        }
        Commit();
    }

    public void RemoveNodes(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        lock (_gate) _state.Nodes.RemoveAll(n => set.Contains(n.Id));
        Commit();
    }

    /// <summary>Record a probe result without disturbing anything else about the node.</summary>
    public void SetLatency(Guid id, int latencyMs)
    {
        lock (_gate)
        {
            var index = _state.Nodes.FindIndex(n => n.Id == id);
            if (index < 0) return;
            _state.Nodes[index] = _state.Nodes[index] with { LatencyMs = latencyMs };
        }
        Commit();
    }

    /// <summary>
    /// Replace a subscription's nodes with a freshly fetched set. Nodes whose share link is
    /// unchanged keep their existing id and measured latency, so the selected server survives
    /// an update and the list does not visibly churn.
    /// </summary>
    /// <remarks>
    /// Both sides are deduplicated by fingerprint. Real subscription feeds repeat entries -
    /// the same endpoint listed twice under different remarks, or a file that concatenates
    /// several mirrors - so a duplicate key here is normal input, not a defect. Building the
    /// carry-over map with ToDictionary made it fatal: one repeated link aborted the whole
    /// update, and once a repeat had been stored, every later update of that subscription
    /// threw as well.
    /// </remarks>
    /// <returns>The number of distinct nodes actually stored, which may be fewer than
    /// <paramref name="incoming"/> when the feed repeats itself.</returns>
    public int ReplaceSubscriptionNodes(Guid subscriptionId, IReadOnlyList<ProxyNode> incoming)
    {
        int stored;
        lock (_gate)
        {
            // First occurrence wins: any of the duplicates carries the same identity, and the
            // earlier one is the one whose id the rest of the app may already be holding.
            var previous = new Dictionary<string, ProxyNode>(StringComparer.Ordinal);
            foreach (var node in _state.Nodes)
            {
                if (node.SubscriptionId != subscriptionId) continue;
                previous.TryAdd(Fingerprint(node), node);
            }

            _state.Nodes.RemoveAll(n => n.SubscriptionId == subscriptionId);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in incoming)
            {
                var key = Fingerprint(node);
                if (!seen.Add(key)) continue;

                var carried = previous.TryGetValue(key, out var old)
                    ? node with { Id = old.Id, LatencyMs = old.LatencyMs }
                    : node;

                _state.Nodes.Add(carried with
                {
                    SubscriptionId = subscriptionId,
                    Remark = Disambiguate(carried.Remark, names),
                });
            }

            stored = seen.Count;

            var subscription = _state.Subscriptions.FirstOrDefault(s => s.Id == subscriptionId);
            if (subscription is not null) subscription.NodeCount = stored;
        }
        Commit();
        return stored;
    }

    /// <summary>
    /// Make a remark unique within its subscription by suffixing repeats.
    /// </summary>
    /// <remarks>
    /// The other half of duplicate handling. Two entries that share a fingerprint are the
    /// same server and one of them is dropped; two that share only a label are different
    /// servers a provider happened to name alike ("Netherlands" appearing four times), and
    /// dropping those would lose servers the user can reach. Renaming keeps them all and
    /// makes the list selectable, and it is safe to redo on every update because the label
    /// is not part of the fingerprint - a node keeps its id and latency regardless.
    /// </remarks>
    private static string Disambiguate(string remark, Dictionary<string, int> used)
    {
        // A blank remark displays as the endpoint, which is unique by construction.
        if (string.IsNullOrWhiteSpace(remark)) return remark;

        if (used.TryAdd(remark, 1)) return remark;

        // Start from the highest suffix issued so far, and keep going if the feed itself
        // already contains a name shaped like the one we are about to invent.
        var n = used[remark];
        string candidate;
        do
        {
            n++;
            candidate = $"{remark} ({n})";
        }
        while (!used.TryAdd(candidate, 1));

        used[remark] = n;
        return candidate;
    }

    /// <summary>Remove nodes that differ only by id, keeping the first occurrence.</summary>
    public int Deduplicate()
    {
        int removed;
        lock (_gate)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var kept = new List<ProxyNode>(_state.Nodes.Count);
            foreach (var node in _state.Nodes)
            {
                if (seen.Add(Fingerprint(node))) kept.Add(node);
            }
            removed = _state.Nodes.Count - kept.Count;
            _state.Nodes = kept;
        }
        if (removed > 0) Commit();
        return removed;
    }

    public void UpsertSubscription(Subscription subscription)
    {
        lock (_gate)
        {
            var index = _state.Subscriptions.FindIndex(s => s.Id == subscription.Id);
            if (index >= 0) _state.Subscriptions[index] = subscription;
            else _state.Subscriptions.Add(subscription);
        }
        Commit();
    }

    public void RemoveSubscription(Guid id, bool removeNodes)
    {
        lock (_gate)
        {
            _state.Subscriptions.RemoveAll(s => s.Id == id);
            if (removeNodes) _state.Nodes.RemoveAll(n => n.SubscriptionId == id);
        }
        Commit();
    }

    /// <summary>
    /// Identity of a server as the user thinks of it: same endpoint and credentials means the
    /// same server, even if the remark or a cosmetic parameter changed upstream.
    /// </summary>
    private static string Fingerprint(ProxyNode node)
    {
        var settings = string.Join('|', node.Settings
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"{kv.Key}={kv.Value}"));
        return string.Join("|#|",
            node.Protocol, node.Server, node.Port,
            node.Transport.Kind, node.Transport.Path, node.Transport.Host,
            node.Security.Kind, node.Security.ServerName, node.Security.PublicKey,
            settings);
    }

    /// <summary>Wait for every queued write to reach disk. Call before the process exits.</summary>
    public Task FlushAsync()
    {
        lock (_gate) return _pendingWrite;
    }

    private void Commit()
    {
        ProfileState snapshot;
        lock (_gate)
        {
            snapshot = new ProfileState
            {
                Nodes = [.. _state.Nodes],
                Subscriptions = [.. _state.Subscriptions],
            };

            _pendingWrite = _pendingWrite.ContinueWith(
                _ => _store.SaveAsync(snapshot),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }
        Changed?.Invoke();
    }
}
