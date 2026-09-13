using FCon.Abstractions.Plugins;

[assembly: FConPluginAssembly("Built-in protocols", "1.0.0", Author = "FCon")]

namespace FCon.Plugins.Builtin;

/// <summary>
/// The protocol set 3x-ui can provision. The host loads these directly rather than
/// through the plugin loader, so a broken side-loaded plugin can never take them out.
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
    ];
}
