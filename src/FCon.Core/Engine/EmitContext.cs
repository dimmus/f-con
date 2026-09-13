using System.Text.Json.Nodes;
using FCon.Abstractions.Emit;
using FCon.Abstractions.Model;
using FCon.Abstractions.Plugins;

namespace FCon.Core.Engine;

/// <summary>
/// Host side of <see cref="IEmitContext"/>. Plugins call back into this so the
/// transport, TLS and mux layers are emitted identically no matter who wrote the plugin.
/// </summary>
public sealed class EmitContext(string tag, string? engineVersion = null) : IEmitContext
{
    private readonly List<string> _warnings = [];

    public string Tag { get; } = tag;
    public string? EngineVersion { get; } = engineVersion;

    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>True when the plugin emitted a sing-box endpoint rather than an outbound.</summary>
    public bool IsEndpoint { get; private set; }

    public void DeclareEndpoint() => IsEndpoint = true;

    public void ApplyTransport(JsonObject outbound, ProxyNode node, EngineKind engine)
    {
        if (engine == EngineKind.Xray)
        {
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
