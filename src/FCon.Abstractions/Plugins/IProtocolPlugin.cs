using System.Text.Json.Nodes;
using FCon.Abstractions.Model;

namespace FCon.Abstractions.Plugins;

/// <summary>
/// Contract every protocol plugin implements. Implementations must be stateless and
/// thread-safe; the host caches a single instance per plugin for the process lifetime.
/// </summary>
public interface IProtocolPlugin
{
    ProtocolDescriptor Descriptor { get; }

    /// <summary>Parse a share link into a node. Return false (with a reason) rather than throwing.</summary>
    bool TryParseLink(string link, out ProxyNode? node, out string? error);

    /// <summary>Serialize a node back into its canonical share link.</summary>
    string BuildLink(ProxyNode node);

    /// <summary>Human-readable problems that would stop this node from connecting. Empty = valid.</summary>
    IReadOnlyList<string> Validate(ProxyNode node);

    /// <summary>
    /// Emit the engine-specific outbound object. The host supplies the tag and shared
    /// transport/TLS emitters through <paramref name="ctx"/>.
    /// </summary>
    JsonObject EmitOutbound(ProxyNode node, EngineKind engine, IEmitContext ctx);
}

/// <summary>Services the host lends to a plugin while it emits configuration.</summary>
public interface IEmitContext
{
    /// <summary>Outbound tag the host expects on the emitted object.</summary>
    string Tag { get; }

    /// <summary>Version string of the target core, when known — lets plugins gate new fields.</summary>
    string? EngineVersion { get; }

    /// <summary>
    /// True when the target core is at least the given version. Also true when the version
    /// is unknown (no core installed, preview pane), so gating only bites on a core that is
    /// known to be too old — a plugin should emit the modern form by default.
    /// </summary>
    bool EngineAtLeast(int major, int minor, int patch = 0);

    /// <summary>Apply the node's transport settings to an outbound the plugin is building.</summary>
    void ApplyTransport(JsonObject outbound, ProxyNode node, EngineKind engine);

    /// <summary>Apply the node's TLS/REALITY settings to an outbound the plugin is building.</summary>
    void ApplySecurity(JsonObject outbound, ProxyNode node, EngineKind engine);

    /// <summary>Apply multiplexing settings, when the protocol supports them.</summary>
    void ApplyMux(JsonObject outbound, ProxyNode node, EngineKind engine);

    /// <summary>
    /// Declare that what this plugin emitted is a sing-box <c>endpoint</c> rather than an
    /// <c>outbound</c> — the shape WireGuard and other tunnel-style protocols take from
    /// sing-box 1.11 onward. Ignored for engines that have no such distinction.
    /// </summary>
    void DeclareEndpoint();

    /// <summary>
    /// Emit a helper outbound that the main one detours through — the shape ShadowTLS
    /// takes in sing-box, where a Shadowsocks outbound rides on a separate shadowtls
    /// outbound. The host places it beside the main outbound. Its tag must be unique;
    /// derive it from <see cref="Tag"/> (for example <c>Tag + "-stls"</c>).
    /// </summary>
    void EmitAuxiliary(JsonObject outbound);

    void Warn(string message);
}

/// <summary>Marks an assembly as a FCon plugin package. The host scans only annotated assemblies.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class FConPluginAssemblyAttribute : Attribute
{
    public FConPluginAssemblyAttribute(string name, string version)
    {
        Name = name;
        Version = version;
    }

    public string Name { get; }
    public string Version { get; }
    public string? Author { get; init; }
    /// <summary>Abstractions contract version the plugin was built against.</summary>
    public int ContractVersion { get; init; } = ContractInfo.Version;
}

public static class ContractInfo
{
    /// <summary>Bumped whenever <see cref="IProtocolPlugin"/> or the models change incompatibly.</summary>
    /// <remarks>
    /// v2 added <see cref="IEmitContext.DeclareEndpoint"/>.
    /// v3 added <see cref="IEmitContext.EmitAuxiliary"/> and <see cref="IEmitContext.EngineAtLeast"/>.
    /// </remarks>
    public const int Version = 3;
}
