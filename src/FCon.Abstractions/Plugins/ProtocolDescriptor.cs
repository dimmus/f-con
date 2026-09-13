using FCon.Abstractions.Model;

namespace FCon.Abstractions.Plugins;

/// <summary>Static capability advertisement for one protocol plugin.</summary>
public sealed record ProtocolDescriptor
{
    /// <summary>Stable id, also the value of <see cref="ProxyNode.Protocol"/>. Lowercase.</summary>
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }

    /// <summary>URI schemes this plugin claims, without "://" — e.g. ["vless"].</summary>
    public required IReadOnlyList<string> Schemes { get; init; }

    public EngineSupport Engines { get; init; } = EngineSupport.Both;

    /// <summary>Transports this protocol may be layered on. Empty = transport not applicable.</summary>
    public IReadOnlyList<TransportKind> Transports { get; init; } = [];

    /// <summary>Security layers this protocol accepts. Empty = not applicable.</summary>
    public IReadOnlyList<SecurityKind> Security { get; init; } = [];

    public bool SupportsMux { get; init; }

    /// <summary>Protocol-specific editable fields, in display order.</summary>
    public IReadOnlyList<FieldSpec> Fields { get; init; } = [];

    /// <summary>Sort weight in pickers; lower first.</summary>
    public int Order { get; init; } = 100;
}
