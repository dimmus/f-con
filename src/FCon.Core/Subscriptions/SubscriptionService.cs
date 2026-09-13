using System.Net;
using System.Net.Http.Headers;
using FCon.Core.Config;
using FCon.Core.Import;
using FCon.Core.Storage;

namespace FCon.Core.Subscriptions;

public sealed record SubscriptionUpdateResult(
    Subscription Subscription,
    int NodeCount,
    IReadOnlyList<string> Errors,
    string? FatalError = null)
{
    public bool Succeeded => FatalError is null;
}

/// <summary>
/// Fetches subscription URLs and folds the result into the profile store. Handles the
/// two things providers actually vary on: the User-Agent they key their format off, and
/// the Subscription-Userinfo header carrying quota.
/// </summary>
public sealed class SubscriptionService(
    ProfileStore profiles,
    LinkImporter importer,
    Func<AppSettings> settingsAccessor)
{
    /// <summary>Providers commonly gate the response format on a recognised client UA.</summary>
    public const string DefaultUserAgent = "FCon/1.0 (Windows)";

    public async Task<SubscriptionUpdateResult> UpdateAsync(
        Subscription subscription,
        CancellationToken ct = default)
    {
        var settings = settingsAccessor();

        try
        {
            using var client = CreateClient(subscription, settings);
            using var response = await client
                .GetAsync(subscription.Url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return Fail(subscription, $"Server returned {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            ReadUserInfo(subscription, response.Headers);

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var import = importer.Import(body, subscription.Id);

            if (!import.AnySucceeded)
            {
                var reason = import.Errors.Count > 0
                    ? import.Errors[0]
                    : "The response contained no usable server links.";
                return Fail(subscription, reason);
            }

            // The store drops repeats, so its count - not the parsed count - is what the
            // user will see in the server list.
            var stored = profiles.ReplaceSubscriptionNodes(subscription.Id, import.Nodes);

            subscription.LastUpdated = DateTimeOffset.Now;
            subscription.LastError = null;
            subscription.NodeCount = stored;
            profiles.UpsertSubscription(subscription);

            return new SubscriptionUpdateResult(subscription, stored, import.Errors);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(subscription, "The request timed out.");
        }
        catch (HttpRequestException ex)
        {
            return Fail(subscription, ex.Message);
        }
        catch (Exception ex)
        {
            // A malformed feed must not escape to the dispatcher. Anything unexpected here
            // belongs on the subscription row as an error the user can see and retry, not in
            // a modal crash dialog with a stack trace in it.
            return Fail(subscription, $"Could not process the response: {ex.Message}");
        }
    }

    /// <summary>Update every enabled subscription, sequentially so a shared proxy is not hammered.</summary>
    public async Task<IReadOnlyList<SubscriptionUpdateResult>> UpdateAllAsync(
        IProgress<Subscription>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<SubscriptionUpdateResult>();
        foreach (var subscription in profiles.Subscriptions.Where(s => s.Enabled))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(subscription);
            results.Add(await UpdateAsync(subscription, ct).ConfigureAwait(false));
        }
        return results;
    }

    private SubscriptionUpdateResult Fail(Subscription subscription, string error)
    {
        subscription.LastError = error;
        subscription.LastUpdated = DateTimeOffset.Now;
        profiles.UpsertSubscription(subscription);
        return new SubscriptionUpdateResult(subscription, 0, [], error);
    }

    private static HttpClient CreateClient(Subscription subscription, AppSettings settings)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        };

        if (subscription.UpdateThroughProxy)
        {
            handler.Proxy = new WebProxy($"http://127.0.0.1:{settings.HttpPort}");
            handler.UseProxy = true;
        }
        else
        {
            // Bypass whatever system proxy we may have set for ourselves.
            handler.UseProxy = false;
        }

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            string.IsNullOrWhiteSpace(subscription.UserAgent) ? DefaultUserAgent : subscription.UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

    /// <summary>
    /// Parse the de-facto standard quota header:
    /// <c>upload=0; download=1234; total=5678; expire=1700000000</c>.
    /// </summary>
    internal static void ReadUserInfo(Subscription subscription, HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Subscription-Userinfo", out var values)) return;

        long upload = 0, download = 0, total = 0, expire = 0;
        foreach (var part in string.Join(';', values).Split(';', StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq].Trim().ToLowerInvariant();
            if (!long.TryParse(part[(eq + 1)..].Trim(), out var value)) continue;

            switch (key)
            {
                case "upload": upload = value; break;
                case "download": download = value; break;
                case "total": total = value; break;
                case "expire": expire = value; break;
            }
        }

        subscription.UsedBytes = upload + download;
        subscription.TotalBytes = total > 0 ? total : null;
        subscription.ExpiresAt = expire > 0
            ? DateTimeOffset.FromUnixTimeSeconds(expire)
            : null;
    }
}
