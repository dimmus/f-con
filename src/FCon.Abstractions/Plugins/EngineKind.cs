namespace FCon.Abstractions.Plugins;

/// <summary>Proxy core that will consume the emitted outbound JSON.</summary>
public enum EngineKind
{
    /// <summary>sing-box — default engine (native TUN, rule-sets).</summary>
    SingBox,
    /// <summary>Xray-core — origin of VLESS / XTLS Vision / REALITY.</summary>
    Xray,
}

[Flags]
public enum EngineSupport
{
    None = 0,
    SingBox = 1,
    Xray = 2,
    Both = SingBox | Xray,
}
