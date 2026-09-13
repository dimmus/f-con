using System.Reflection;
using System.Runtime.Loader;
using FCon.Abstractions.Model;
using FCon.Abstractions.Plugins;
using FCon.Plugins.Builtin;

namespace FCon.Core.Plugins;

public sealed record LoadedPlugin(IProtocolPlugin Instance, string Source, bool IsBuiltin)
{
    public ProtocolDescriptor Descriptor => Instance.Descriptor;
}

public sealed record PluginLoadFailure(string Path, string Reason);

/// <summary>
/// Owns the protocol plugin set: built-ins compiled in, plus any side-loaded packages
/// found under <see cref="AppPaths.PluginsDirectory"/>. Lookups are by protocol id and
/// by URI scheme, both case-insensitive.
/// </summary>
public sealed class PluginRegistry
{
    private readonly Dictionary<string, LoadedPlugin> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LoadedPlugin> _byScheme = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PluginLoadFailure> _failures = [];

    public IReadOnlyList<LoadedPlugin> Plugins => [.. _byId.Values.OrderBy(p => p.Descriptor.Order)];

    /// <summary>Plugin packages that could not be loaded, for surfacing in Settings.</summary>
    public IReadOnlyList<PluginLoadFailure> Failures => _failures;

    public static PluginRegistry CreateDefault(bool loadExternal = true)
    {
        var registry = new PluginRegistry();
        foreach (var plugin in BuiltinPlugins.Create())
            registry.Register(plugin, "built-in", isBuiltin: true);

        if (loadExternal && Directory.Exists(AppPaths.PluginsDirectory))
            registry.LoadExternal(AppPaths.PluginsDirectory);

        return registry;
    }

    public void Register(IProtocolPlugin plugin, string source, bool isBuiltin)
    {
        var descriptor = plugin.Descriptor;
        var entry = new LoadedPlugin(plugin, source, isBuiltin);

        if (_byId.TryGetValue(descriptor.Id, out var existing))
        {
            // Built-ins win: a side-loaded package must not silently shadow a core protocol.
            if (existing.IsBuiltin)
            {
                _failures.Add(new PluginLoadFailure(source,
                    $"Protocol id \"{descriptor.Id}\" is already provided by a built-in plugin."));
                return;
            }
        }

        _byId[descriptor.Id] = entry;
        foreach (var scheme in descriptor.Schemes)
        {
            if (_byScheme.TryGetValue(scheme, out var owner) && owner.IsBuiltin && !isBuiltin)
            {
                _failures.Add(new PluginLoadFailure(source,
                    $"Scheme \"{scheme}\" is already claimed by {owner.Descriptor.DisplayName}."));
                continue;
            }
            _byScheme[scheme] = entry;
        }
    }

    public IProtocolPlugin? ById(string? protocolId) =>
        protocolId is not null && _byId.TryGetValue(protocolId, out var p) ? p.Instance : null;

    public IProtocolPlugin? ByScheme(string? scheme) =>
        scheme is not null && _byScheme.TryGetValue(scheme, out var p) ? p.Instance : null;

    /// <summary>Resolve the plugin that owns a link by its scheme prefix.</summary>
    public IProtocolPlugin? ByLink(string link)
    {
        var idx = link.IndexOf("://", StringComparison.Ordinal);
        return idx <= 0 ? null : ByScheme(link[..idx].Trim().ToLowerInvariant());
    }

    public IProtocolPlugin Require(ProxyNode node) =>
        ById(node.Protocol)
        ?? throw new InvalidOperationException(
            $"No plugin is loaded for protocol \"{node.Protocol}\". "
            + "The server was probably imported while a plugin was installed that is now missing.");

    // ------------------------------------------------------------- discovery

    private void LoadExternal(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            foreach (var dll in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
            {
                TryLoadAssembly(dll);
            }
        }
    }

    private void TryLoadAssembly(string path)
    {
        try
        {
            var context = new PluginLoadContext(path);
            var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(path));

            var marker = assembly.GetCustomAttribute<FConPluginAssemblyAttribute>();
            if (marker is null) return; // Not a plugin package — a dependency of one.

            if (marker.ContractVersion != ContractInfo.Version)
            {
                _failures.Add(new PluginLoadFailure(path,
                    $"Built against plugin contract v{marker.ContractVersion}; this build requires v{ContractInfo.Version}."));
                return;
            }

            var found = 0;
            foreach (var type in assembly.GetExportedTypes())
            {
                if (type.IsAbstract || !typeof(IProtocolPlugin).IsAssignableFrom(type)) continue;
                if (type.GetConstructor(Type.EmptyTypes) is null)
                {
                    _failures.Add(new PluginLoadFailure(path,
                        $"{type.Name} has no public parameterless constructor."));
                    continue;
                }

                if (Activator.CreateInstance(type) is IProtocolPlugin plugin)
                {
                    Register(plugin, path, isBuiltin: false);
                    found++;
                }
            }

            if (found == 0)
                _failures.Add(new PluginLoadFailure(path, "Package declares no IProtocolPlugin types."));
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException
                                       or ReflectionTypeLoadException or TypeLoadException)
        {
            _failures.Add(new PluginLoadFailure(path, ex.Message));
        }
    }
}

/// <summary>
/// Isolates a plugin package so it can carry its own dependency versions, while the
/// contract assembly is deliberately resolved from the host to keep types identical.
/// </summary>
internal sealed class PluginLoadContext(string pluginPath) : AssemblyLoadContext(isCollectible: true)
{
    private readonly AssemblyDependencyResolver _resolver = new(pluginPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Never load a second copy of the contract: shared types must be reference-equal.
        if (assemblyName.Name is "FCon.Abstractions") return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
    }
}
