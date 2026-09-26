using System.Text.Json.Nodes;
using FCon.Abstractions.Model;

namespace FCon.Core.Engine;

/// <summary>One server as it appears inside a generated config.</summary>
public sealed record PoolMember(ProxyNode Node, string Tag);

/// <summary>Generated engine configuration plus anything the user should know about it.</summary>
public sealed record GeneratedConfig(JsonObject Root, IReadOnlyList<string> Warnings)
{
    public string ToJson() => Root.ToJsonString(new System.Text.Json.JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    /// <summary>Every server the config carries, primary first.</summary>
    public IReadOnlyList<PoolMember> Members { get; init; } = [];

    /// <summary>Tag of the server the user asked for.</summary>
    public string PrimaryTag { get; init; } = "proxy";

    /// <summary>
    /// The selector group the routing points at when servers are grouped (sing-box).
    /// Switching it through the API changes the server without restarting the core.
    /// Null when the config holds a single server, or for Xray.
    /// </summary>
    public string? SelectorTag { get; init; }

    /// <summary>The automatic group: sing-box <c>urltest</c> or Xray's least-ping balancer.</summary>
    public string? AutoTag { get; init; }

    /// <summary>True when the config starts on the automatic group rather than the primary.</summary>
    public bool StartsOnAuto { get; init; }

    public bool IsGrouped => AutoTag is not null;

    public string? TagFor(Guid nodeId) => Members.FirstOrDefault(m => m.Node.Id == nodeId)?.Tag;

    public ProxyNode? NodeFor(string? tag) =>
        tag is null ? null : Members.FirstOrDefault(m => m.Tag.Equals(tag, StringComparison.Ordinal))?.Node;
}
