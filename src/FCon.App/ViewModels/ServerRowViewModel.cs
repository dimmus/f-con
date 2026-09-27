using CommunityToolkit.Mvvm.ComponentModel;
using FCon.Abstractions.Model;
using FCon.Abstractions.Plugins;
using FCon.Core.Localization;

namespace FCon.App.ViewModels;

/// <summary>Display projection of a <see cref="ProxyNode"/> for the server grid.</summary>
public sealed partial class ServerRowViewModel : ObservableObject
{
    [ObservableProperty] private ProxyNode _node;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string? _subscriptionName;
    [ObservableProperty] private FCon.Core.Health.NodeQuality? _quality;

    public ServerRowViewModel(ProxyNode node, ProtocolDescriptor? descriptor)
    {
        _node = node;
        Descriptor = descriptor;
    }

    public ProtocolDescriptor? Descriptor { get; }

    public Guid Id => Node.Id;
    public string Name => Node.DisplayName;
    public string ProtocolLabel => Descriptor?.DisplayName ?? Node.Protocol;
    public string Address => Node.Endpoint;
    public string Group => Node.Group ?? SubscriptionName ?? "";

    /// <summary>Compact "transport + security" summary, the thing users actually scan for.</summary>
    public string Stack
    {
        get
        {
            var transport = Node.Transport.Kind switch
            {
                TransportKind.Raw => Node.Transport.Obfs == HeaderObfs.Http ? "tcp+http" : "tcp",
                TransportKind.Kcp => "mkcp",
                TransportKind.WebSocket => "ws",
                TransportKind.Http2 => "h2",
                TransportKind.Quic => "quic",
                TransportKind.Grpc => "grpc",
                TransportKind.HttpUpgrade => "httpupgrade",
                TransportKind.XHttp => "xhttp",
                _ => "-",
            };

            var security = Node.Security.Kind switch
            {
                SecurityKind.Tls => "tls",
                SecurityKind.Reality => "reality",
                _ => null,
            };

            var flow = Node.Get("flow") is { Length: > 0 } f && f.Contains("vision") ? "vision" : null;

            // WireGuard has no transport or TLS layer to describe.
            if (Descriptor?.Transports.Count == 0) return "-";

            return string.Join(" · ", new[] { transport, security, flow }.Where(s => s is { Length: > 0 }));
        }
    }

    public int? LatencyMs => Node.LatencyMs;

    public string LatencyText => Node.LatencyMs switch
    {
        null => "",
        < 0 => L.T("Row_Timeout"),
        var ms => $"{ms} ms",
    };

    /// <summary>Bucket used by the grid to colour the latency cell.</summary>
    public string LatencyGrade => Node.LatencyMs switch
    {
        null => "none",
        < 0 => "bad",
        < 150 => "good",
        < 400 => "warn",
        _ => "bad",
    };

    partial void OnNodeChanged(ProxyNode value)
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Address));
        OnPropertyChanged(nameof(Stack));
        OnPropertyChanged(nameof(LatencyMs));
        OnPropertyChanged(nameof(LatencyText));
        OnPropertyChanged(nameof(LatencyGrade));
        OnPropertyChanged(nameof(Group));
        OnPropertyChanged(nameof(ProtocolLabel));
    }

    partial void OnSubscriptionNameChanged(string? value) => OnPropertyChanged(nameof(Group));

    partial void OnQualityChanged(FCon.Core.Health.NodeQuality? value)
    {
        OnPropertyChanged(nameof(QualityText));
        OnPropertyChanged(nameof(QualityGrade));
        OnPropertyChanged(nameof(QualityDetail));
    }

    /// <summary>
    /// What experience says about this server, as distinct from a single latency reading:
    /// whether traffic has actually gone through it, and how often it has let us down.
    /// </summary>
    public string QualityText
    {
        get
        {
            if (Quality is not { } q) return "";
            if (q.IsQuarantined(DateTimeOffset.Now)) return L.T("Row_Resting");
            if (!q.Verified) return q.Failures > 0 ? L.F("Row_Fail", q.Failures) : L.T("Row_Untried");

            var reliability = (int)Math.Round(q.Reliability * 100);
            return q.Failures == 0 ? L.T("Row_Reliable") : $"{reliability}%";
        }
    }

    /// <summary>
    /// The counts behind the Record column, for the tooltip.
    /// </summary>
    /// <remarks>
    /// The percentage is Laplace-smoothed - (ok + 1) / (ok + failed + 2) - so a single
    /// success cannot outrank a long good history, and an untried server is not assumed
    /// perfect. That is right for ranking but surprising to read (3 of 4 shows as 67%),
    /// so the raw tally is worth showing next to it.
    /// </remarks>
    public string QualityDetail
    {
        get
        {
            if (Quality is not { } q)
                return L.T("Row_NoRecord");

            var parts = new List<string> { L.F("Row_OkFailed", q.Successes, q.Failures) };

            if (q.LatencyMs is { } latency) parts.Add(L.F("Row_Typical", latency.ToString("0")));
            if (q.JitterMs is { } jitter) parts.Add(L.F("Row_Jitter", jitter.ToString("0")));
            if (q.IsQuarantined(DateTimeOffset.Now)) parts.Add(L.T("Row_RestingAfter"));
            if (q.LastError is { Length: > 0 } error) parts.Add(L.F("Row_LastError", error));

            return string.Join("  ·  ", parts);
        }
    }

    public string QualityGrade
    {
        get
        {
            if (Quality is not { } q) return "none";
            if (q.IsQuarantined(DateTimeOffset.Now)) return "bad";
            if (!q.Verified) return q.Failures > 0 ? "warn" : "none";
            return q.Reliability switch
            {
                >= 0.85 => "good",
                >= 0.6 => "warn",
                _ => "bad",
            };
        }
    }

    /// <summary>Free-text match used by the search box.</summary>
    public bool Matches(string query) =>
        Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Address.Contains(query, StringComparison.OrdinalIgnoreCase)
        || ProtocolLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Group.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Stack.Contains(query, StringComparison.OrdinalIgnoreCase);
}
