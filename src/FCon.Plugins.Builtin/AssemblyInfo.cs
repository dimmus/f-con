using System.Runtime.CompilerServices;
using FCon.Abstractions.Plugins;

[assembly: InternalsVisibleTo("FCon.Core.Tests")]

[assembly: FConPluginAssembly("Built-in protocols", "1.0.0", Author = "FCon")]

namespace FCon.Plugins.Builtin;

/// <summary>
/// The protocol set 3x-ui can provision, plus the sing-box-only QUIC and TLS-camouflage
/// protocols (Hysteria 2, TUIC, AnyTLS; ShadowTLS rides on Shadowsocks). The host loads
/// these directly rather than through the plugin loader, so a broken side-loaded plugin
/// can never take them out.
/// </summary>
public static class BuiltinPlugins
{
    public static IReadOnlyList<IProtocolPlugin> Create() =>
    [
        new VlessPlugin(),
        new VmessPlugin(),
        new TrojanPlugin(),
        new ShadowsocksPlugin(),
        new SocksPlugin(),
        new HttpProxyPlugin(),
        new WireGuardPlugin(),
        new Hysteria2Plugin(),
        new TuicPlugin(),
        new AnyTlsPlugin(),
    ];
}
