using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using FCon.Abstractions.Model;
using FCon.Core.Config;

namespace FCon.App.Views;

/// <summary>Visible when the bound value equals the converter parameter. Drives the nav rail.</summary>
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value?.ToString(), parameter?.ToString()) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Two-way equality, for binding a RadioButton group to a single selection property.</summary>
public sealed class EqualsToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value?.ToString(), parameter?.ToString());

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is not null
            ? System.Convert.ChangeType(parameter, targetType, culture)
            : Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (parameter?.ToString() == "invert") flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is not true;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => value is not true;
}

/// <summary>Collapses an element when the bound string is null or blank.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var empty = string.IsNullOrWhiteSpace(value as string);
        if (parameter?.ToString() == "invert") empty = !empty;
        return empty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Maps a status grade ("good"/"warn"/"bad") to the palette brush for it.</summary>
public sealed class GradeBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value?.ToString() switch
        {
            "good" => "Status.Good",
            "warn" => "Status.Warn",
            "bad" => "Status.Bad",
            _ => "Text.Secondary",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Connection state to the status dot colour.</summary>
public sealed class StateBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            Core.Engine.ConnectionState.Connected => "Status.Good",
            Core.Engine.ConnectionState.Connecting => "Status.Warn",
            Core.Engine.ConnectionState.Disconnecting => "Status.Warn",
            Core.Engine.ConnectionState.Faulted => "Status.Bad",
            _ => "Status.Idle",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Human labels for the enums the UI exposes directly in pickers.</summary>
public sealed class EnumLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        TransportKind t => t switch
        {
            TransportKind.Raw => "TCP (raw)",
            TransportKind.Kcp => "mKCP",
            TransportKind.WebSocket => "WebSocket",
            TransportKind.Http2 => "HTTP/2",
            TransportKind.Quic => "QUIC",
            TransportKind.Grpc => "gRPC",
            TransportKind.HttpUpgrade => "HTTPUpgrade",
            TransportKind.XHttp => "XHTTP",
            _ => t.ToString(),
        },
        SecurityKind s => s switch
        {
            SecurityKind.None => "None",
            SecurityKind.Tls => "TLS",
            SecurityKind.Reality => "REALITY",
            _ => s.ToString(),
        },
        TrafficMode m => m switch
        {
            TrafficMode.SystemProxy => "System proxy",
            TrafficMode.Tun => "TUN (whole system)",
            TrafficMode.Manual => "Manual (listeners only)",
            _ => m.ToString(),
        },
        RoutingMode r => r switch
        {
            RoutingMode.Global => "Everything through proxy",
            RoutingMode.Rules => "Use rules",
            RoutingMode.Direct => "Everything direct",
            _ => r.ToString(),
        },
        RuleAction a => a.ToString(),
        Abstractions.Plugins.EngineKind e => e == Abstractions.Plugins.EngineKind.SingBox ? "sing-box" : "Xray",
        _ => value?.ToString() ?? "",
    };

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}
