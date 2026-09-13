using FCon.Abstractions.Model;
using FCon.Abstractions.Util;
using FCon.Core.Plugins;

namespace FCon.Core.Import;

public sealed record ImportResult(
    IReadOnlyList<ProxyNode> Nodes,
    IReadOnlyList<string> Errors)
{
    public static ImportResult Empty { get; } = new([], []);
    public bool AnySucceeded => Nodes.Count > 0;
}

/// <summary>
/// Turns pasted text or a fetched subscription body into nodes. Accepts a single link,
/// a newline-separated list, or the base64 blob that subscription endpoints return —
/// including base64 that wraps a further list of links.
/// </summary>
public sealed class LinkImporter(PluginRegistry registry)
{
    public ImportResult Import(string text, Guid? subscriptionId = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return ImportResult.Empty;

        var nodes = new List<ProxyNode>();
        var errors = new List<string>();

        foreach (var line in Unwrap(text))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith("//", StringComparison.Ordinal))
                continue;

            var plugin = registry.ByLink(trimmed);
            if (plugin is null)
            {
                errors.Add($"No plugin handles \"{Scheme(trimmed)}\": {Ellipsis(trimmed)}");
                continue;
            }

            if (plugin.TryParseLink(trimmed, out var node, out var error) && node is not null)
            {
                nodes.Add(subscriptionId is null ? node : node with { SubscriptionId = subscriptionId });
            }
            else
            {
                errors.Add($"{error ?? "Could not parse link."} ({Ellipsis(trimmed)})");
            }
        }

        return new ImportResult(nodes, errors);
    }

    /// <summary>Validate a node against the plugin that owns it.</summary>
    public IReadOnlyList<string> Validate(ProxyNode node)
    {
        var plugin = registry.ById(node.Protocol);
        return plugin is null
            ? [$"No plugin is loaded for protocol \"{node.Protocol}\"."]
            : plugin.Validate(node);
    }

    /// <summary>
    /// Yield candidate link lines, transparently decoding a whole-body base64 wrapper.
    /// Subscription servers vary: some return base64 of a link list, some plain text.
    /// </summary>
    private static IEnumerable<string> Unwrap(string text)
    {
        var trimmed = text.Trim();

        if (!trimmed.Contains("://", StringComparison.Ordinal)
            && UriKit.LooksBase64(trimmed)
            && UriKit.TryDecodeBase64(trimmed) is { } decoded)
        {
            trimmed = decoded;
        }

        return trimmed.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string Scheme(string link)
    {
        var idx = link.IndexOf("://", StringComparison.Ordinal);
        return idx <= 0 ? "?" : link[..idx];
    }

    private static string Ellipsis(string value) =>
        value.Length <= 48 ? value : value[..48] + "...";
}
