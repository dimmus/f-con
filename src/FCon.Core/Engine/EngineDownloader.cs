using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using FCon.Abstractions.Plugins;

namespace FCon.Core.Engine;

public sealed record EngineDownloadResult(bool Succeeded, string Message, string? Version = null);

/// <summary>
/// Fetches a stable core release from GitHub and installs it under <c>engines/</c>.
///
/// The archive is never trusted on arrival: its SHA-256 is compared against what the
/// release publishes — the asset digest GitHub's API reports, and for Xray also the
/// <c>.dgst</c> file that ships beside the archive. No published checksum means no
/// install. Only <c>releases/latest</c> is used, which excludes pre-releases.
/// </summary>
public sealed partial class EngineDownloader(Func<int?> proxyPort)
{
    private const string UserAgent = "FCon/1.0 (+https://github.com)";

    public static string ReleasesPage(EngineKind kind) => kind == EngineKind.Xray
        ? "https://github.com/XTLS/Xray-core/releases"
        : "https://github.com/SagerNet/sing-box/releases";

    private static string ApiLatest(EngineKind kind) => kind == EngineKind.Xray
        ? "https://api.github.com/repos/XTLS/Xray-core/releases/latest"
        : "https://api.github.com/repos/SagerNet/sing-box/releases/latest";

    /// <summary>Look up the newest stable version without downloading anything.</summary>
    public async Task<string?> LatestVersionAsync(EngineKind kind, CancellationToken ct = default)
    {
        try
        {
            using var http = CreateClient();
            var release = await FetchReleaseAsync(http, kind, ct).ConfigureAwait(false);
            return release?.Version;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    public async Task<EngineDownloadResult> InstallAsync(
        EngineKind kind,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var targetDir = Path.Combine(AppPaths.EnginesDirectory, EngineLocator.DirectoryName(kind));
        var exeName = EngineLocator.ExecutableName(kind);

        try
        {
            Directory.CreateDirectory(targetDir);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return new EngineDownloadResult(false, $"Cannot write to {targetDir}: {ex.Message}");
        }

        try
        {
            using var http = CreateClient();

            progress?.Report("Looking up the latest release...");
            var release = await FetchReleaseAsync(http, kind, ct).ConfigureAwait(false);
            if (release is null)
                return new EngineDownloadResult(false, "The release listing had no Windows amd64 archive.");

            var current = EngineLocator.Describe(kind);
            if (current is not null && SameVersion(current.Version, release.Version))
                return new EngineDownloadResult(true, $"{release.Version} is already installed.", release.Version);

            // Collect every checksum the release publishes before trusting the download.
            var expected = new List<string>();
            if (release.Digest is { } digest) expected.Add(digest);
            if (release.ChecksumUrl is { } dgstUrl)
            {
                progress?.Report("Fetching the published checksum...");
                var dgst = await http.GetStringAsync(dgstUrl, ct).ConfigureAwait(false);
                if (ParseDgst(dgst) is { } fromFile) expected.Add(fromFile);
            }
            if (expected.Count == 0)
            {
                return new EngineDownloadResult(false,
                    $"{release.Version} publishes no SHA-256 checksum, so the download cannot be verified. "
                    + "Install it by hand from the releases page if you trust it.");
            }

            var downloadDir = Path.Combine(AppPaths.RuntimeDirectory, "download");
            Directory.CreateDirectory(downloadDir);
            var archive = Path.Combine(downloadDir, release.AssetName);

            progress?.Report($"Downloading {release.AssetName}...");
            var actual = await DownloadAsync(http, release.AssetUrl, archive, release.Size, progress, ct).ConfigureAwait(false);

            foreach (var hash in expected)
            {
                if (!hash.Equals(actual, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(archive);
                    return new EngineDownloadResult(false,
                        "Checksum mismatch: the downloaded archive does not match what the release "
                        + "publishes. Nothing was installed.");
                }
            }

            progress?.Report("Verified. Installing...");
            var installed = Extract(kind, archive, targetDir, exeName);
            TryDelete(archive);

            if (!installed)
                return new EngineDownloadResult(false, $"{exeName} was not found inside the archive.");

            var info = EngineLocator.Describe(kind);
            var banner = info?.Version ?? release.Version;
            return new EngineDownloadResult(true, $"Installed {banner} to {targetDir}.", release.Version);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new EngineDownloadResult(false, "Cancelled.");
        }
        catch (HttpRequestException ex)
        {
            var hint = proxyPort() is null
                ? " GitHub may be blocked on this network; connect through a working server first and try again."
                : "";
            return new EngineDownloadResult(false, $"Download failed: {ex.Message}.{hint}");
        }
        catch (Exception ex) when (ex is TaskCanceledException or JsonException or IOException
                                       or UnauthorizedAccessException or InvalidDataException)
        {
            return new EngineDownloadResult(false, $"Download failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------- helpers

    private sealed record ReleaseAsset(
        string Version,
        string AssetName,
        string AssetUrl,
        long Size,
        string? Digest,
        string? ChecksumUrl);

    private static async Task<ReleaseAsset?> FetchReleaseAsync(HttpClient http, EngineKind kind, CancellationToken ct)
    {
        using var response = await http.GetAsync(ApiLatest(kind), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = document.RootElement;

        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var version = tag.TrimStart('v', 'V');
        if (version.Length == 0) return null;

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

        var wanted = kind == EngineKind.Xray
            ? "Xray-windows-64.zip"
            : $"sing-box-{version}-windows-amd64.zip";

        string? url = null, digest = null, checksumUrl = null;
        long size = 0;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            {
                url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                if (asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String)
                {
                    var value = d.GetString() ?? "";
                    if (value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        digest = value["sha256:".Length..];
                }
            }
            else if (name.Equals(wanted + ".dgst", StringComparison.OrdinalIgnoreCase))
            {
                checksumUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            }
        }

        return url is null ? null : new ReleaseAsset(version, wanted, url, size, digest, checksumUrl);
    }

    /// <summary>Stream to disk while hashing, so the file is never read twice.</summary>
    private static async Task<string> DownloadAsync(
        HttpClient http,
        string url,
        string path,
        long expectedSize,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? expectedSize;
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[1 << 16];
        long done = 0;
        var lastReport = 0L;
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0) break;
            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            sha.AppendData(buffer, 0, read);
            done += read;

            if (progress is not null && done - lastReport >= 2 * 1024 * 1024)
            {
                lastReport = done;
                progress.Report(total > 0
                    ? $"Downloading... {done * 100 / total}% of {total / 1048576} MB"
                    : $"Downloading... {done / 1048576} MB");
            }
        }

        return Convert.ToHexString(sha.GetHashAndReset());
    }

    /// <summary>
    /// Copy the executable, licence and (for Xray) the geo assets out of the archive.
    /// Written to a temp name and swapped in, so a half-extracted core never replaces a
    /// working one. A core that is currently running holds its file locked, which
    /// surfaces as an IOException the caller reports.
    /// </summary>
    private static bool Extract(EngineKind kind, string archive, string targetDir, string exeName)
    {
        using var zip = ZipFile.OpenRead(archive);
        var found = false;

        foreach (var entry in zip.Entries)
        {
            if (entry.Name.Length == 0) continue; // directory
            var name = entry.Name;

            string? destination = null;
            if (name.Equals(exeName, StringComparison.OrdinalIgnoreCase))
            {
                destination = Path.Combine(targetDir, exeName);
                found = true;
            }
            else if (name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase))
            {
                destination = Path.Combine(targetDir, name);
            }
            else if (kind == EngineKind.Xray
                     && (name.Equals("geoip.dat", StringComparison.OrdinalIgnoreCase)
                         || name.Equals("geosite.dat", StringComparison.OrdinalIgnoreCase)))
            {
                Directory.CreateDirectory(AppPaths.AssetsDirectory);
                destination = Path.Combine(AppPaths.AssetsDirectory, name);
            }
            else if (kind == EngineKind.Xray && name.Equals("wintun.dll", StringComparison.OrdinalIgnoreCase))
            {
                destination = Path.Combine(targetDir, name);
            }

            if (destination is null) continue;

            var temp = destination + ".new";
            entry.ExtractToFile(temp, overwrite: true);
            File.Move(temp, destination, overwrite: true);
        }

        return found;
    }

    /// <summary>Xray's <c>.dgst</c> lists several digests; take the SHA-256 line.</summary>
    internal static string? ParseDgst(string text)
    {
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var match = DgstSha256().Match(line);
            if (match.Success) return match.Groups["hash"].Value;
        }
        return null;
    }

    private static bool SameVersion(string? banner, string version) =>
        banner is not null && banner.Contains(version, StringComparison.OrdinalIgnoreCase);

    private HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
        };

        // Through the tunnel when one is up: the whole point of the app is that GitHub
        // may not be reachable directly.
        if (proxyPort() is { } port and > 0)
        {
            handler.Proxy = new WebProxy($"http://127.0.0.1:{port}");
            handler.UseProxy = true;
        }
        else
        {
            handler.UseProxy = false;
        }

        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json, */*");
        return http;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover archive in the runtime folder is harmless.
        }
    }

    [GeneratedRegex(@"^\s*SHA2?-?256\s*[=:]\s*(?<hash>[0-9a-fA-F]{64})\s*$")]
    private static partial Regex DgstSha256();
}
