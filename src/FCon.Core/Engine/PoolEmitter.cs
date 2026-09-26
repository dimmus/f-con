using System.Text.Json.Nodes;
using FCon.Abstractions.Model;
using FCon.Abstractions.Plugins;
using FCon.Core.Plugins;

namespace FCon.Core.Engine;

/// <summary>One server's emitted outbound, plus the helper outbounds it detours through.</summary>
public sealed record EmittedNode(
    ProxyNode Node,
    string Tag,
    JsonObject Outbound,
    bool IsEndpoint,
    IReadOnlyList<JsonObject> Auxiliary);

/// <summary>
/// Emits a set of servers for one engine. The primary is strict: a server the user
/// explicitly chose fails loudly. The rest of the pool is best-effort: a member the
/// engine cannot run is dropped with a note, because one unusual server must not stop
/// the failover group from forming.
/// </summary>
public static class PoolEmitter
{
    /// <summary>Tag prefix shared by every grouped server; Xray's balancer selects on it.</summary>
    public const string NodeTagPrefix = "node-";

    public static IReadOnlyList<EmittedNode> Emit(
        PluginRegistry registry,
        ProxyNode primary,
        IReadOnlyList<ProxyNode> pool,
        EngineKind engine,
        string? engineVersion,
        string primaryTag,
        List<string> warnings)
    {
        var used = new HashSet<string>(StringComparer.Ordinal) { primaryTag };
        var result = new List<EmittedNode>();

        // The primary throws on failure, exactly as a single-server config always did.
        var primaryCtx = new EmitContext(primaryTag, engineVersion);
        var primaryOutbound = registry.Require(primary).EmitOutbound(primary, engine, primaryCtx);
        warnings.AddRange(primaryCtx.Warnings);
        result.Add(new EmittedNode(primary, primaryTag, primaryOutbound, primaryCtx.IsEndpoint, primaryCtx.Auxiliary));

        var skipped = new List<string>();
        foreach (var node in pool)
        {
            if (node.Id == primary.Id) continue;

            var plugin = registry.ById(node.Protocol);
            if (plugin is null || !Supports(plugin.Descriptor.Engines, engine))
            {
                skipped.Add(node.DisplayName);
                continue;
            }

            var tag = TagFor(node, used);
            var ctx = new EmitContext(tag, engineVersion);
            try
            {
                var outbound = plugin.EmitOutbound(node, engine, ctx);
                result.Add(new EmittedNode(node, tag, outbound, ctx.IsEndpoint, ctx.Auxiliary));
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                used.Remove(tag);
                skipped.Add($"{node.DisplayName} ({ex.Message.TrimEnd('.')})");
            }
        }

        if (skipped.Count > 0)
        {
            var shown = string.Join(", ", skipped.Take(3));
            var more = skipped.Count > 3 ? $" and {skipped.Count - 3} more" : "";
            warnings.Add($"Left out of the failover group: {shown}{more}.");
        }

        return result;
    }

    public static bool Supports(EngineSupport support, EngineKind engine) =>
        engine == EngineKind.Xray ? support.HasFlag(EngineSupport.Xray) : support.HasFlag(EngineSupport.SingBox);

    /// <summary>Short, stable, unique tag derived from the node id.</summary>
    public static string TagFor(ProxyNode node, HashSet<string> used)
    {
        var stem = NodeTagPrefix + node.Id.ToString("N")[..8];
        var tag = stem;
        var n = 2;
        while (!used.Add(tag)) tag = $"{stem}-{n++}";
        return tag;
    }
}
