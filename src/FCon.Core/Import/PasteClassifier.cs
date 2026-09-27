namespace FCon.Core.Import;

public enum PasteKind
{
    Empty,
    /// <summary>A single http(s) address: a subscription feed to add and fetch.</summary>
    SubscriptionUrl,
    /// <summary>Share links, a list of them, or a base64 payload: servers to import directly.</summary>
    Links,
}

/// <summary>
/// Decides what a pasted clipboard means. Share links carry their own schemes
/// (<c>vless://</c>, <c>ss://</c> ...), so a lone http(s) address can only be a
/// subscription. Anything else is handed to the link importer, which also unwraps
/// base64 subscription bodies pasted as text.
/// </summary>
public static class PasteClassifier
{
    public static PasteKind Classify(string? text, out string normalised)
    {
        normalised = (text ?? "").Trim();
        if (normalised.Length == 0) return PasteKind.Empty;

        var lines = normalised.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 1
            && Uri.TryCreate(lines[0], UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
        {
            normalised = uri.ToString();
            return PasteKind.SubscriptionUrl;
        }

        return PasteKind.Links;
    }
}
