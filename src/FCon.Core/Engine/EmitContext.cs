using System.Text.Json.Nodes;
using FCon.Abstractions.Emit;
using FCon.Abstractions.Model;
using FCon.Abstractions.Plugins;
using FCon.Abstractions.Util;

namespace FCon.Core.Engine;

/// <summary>
/// Host side of <see cref="IEmitContext"/>. Plugins call back into this so the
/// transport, TLS and mux layers are emitted identically no matter who wrote the plugin.
/// </summary>
public sealed class EmitContext(string tag, string? engineVersion = null) : IEmitContext
{
    private readonly List<string> _warnings = [];
    private readonly List<JsonObject> _auxiliary = [];
    private readonly Version? _version = EngineVersionKit.Parse(engineVersion);

    public string Tag { get; } = tag;
    public string? EngineVersion { get; } = engineVersion;

    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Helper outbounds the main one detours through (ShadowTLS).</summary>
    public IReadOnlyList<JsonObject> Auxiliary => _auxiliary;

    /// <summary>True when the plugin emitted a sing-box endpoint rather than an outbound.</summary>
    public bool IsEndpoint { get; private set; }

    public void DeclareEndpoint() => IsEndpoint = true;

    public void EmitAuxiliary(JsonObject outbound) => _auxiliary.Add(outbound);

    public bool EngineAtLeast(int major, int minor, int patch = 0) =>
        EngineVersionKit.AtLeast(_version, major, minor, patch);

    public void ApplyTransport(JsonObject outbound, ProxyNode node, EngineKind engine)
    {
        if (engine == EngineKind.Xray)
        {
            // XHTTP arrived in Xray 24.10.31; older cores reject the transport outright.
            if (node.Transport.Kind == TransportKind.XHttp && !EngineAtLeast(24, 10, 31))
                Warn("XHTTP needs Xray 24.10.31 or newer; this core is older and will refuse the config.");

            TransportEmitter.ApplyXray(outbound, node.Transport);
        }
        else if (TransportEmitter.ApplySingBox(outbound, node.Transport) is { } warning)
        {
            Warn(warning);
        }
    }

    public void ApplySecurity(JsonObject outbound, ProxyNode node, EngineKind engine)
    {
        if (engine == EngineKind.Xray)
        {
            SecurityEmitter.ApplyXray(outbound, node);
        }
        else if (SecurityEmitter.ApplySingBox(outbound, node) is { } warning)
        {
            Warn(warning);
        }
    }

    public void ApplyMux(JsonObject outbound, ProxyNode node, EngineKind engine)
    {
        if (engine == EngineKind.Xray) MuxEmitter.ApplyXray(outbound, node.Mux);
        else MuxEmitter.ApplySingBox(outbound, node.Mux);
    }

    public void Warn(string message)
    {
        if (!_warnings.Contains(message)) _warnings.Add(message);
    }
}
