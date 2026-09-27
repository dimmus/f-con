using System.ComponentModel;
using System.Globalization;

namespace FCon.Core.Localization;

/// <summary>
/// The UI's string table, switchable at runtime. English is the source of truth;
/// a missing translation falls back to it, and a missing key falls back to the key
/// itself so a typo shows up on screen instead of crashing.
///
/// XAML binds to the indexer through the <c>{loc:T Key}</c> markup extension, so
/// changing <see cref="Culture"/> re-renders every bound string; view models listen
/// to <see cref="Changed"/> and re-raise their computed properties.
/// </summary>
public sealed class Localizer : INotifyPropertyChanged
{
    public static Localizer Instance { get; } = new();

    public const string English = "en";
    public const string Russian = "ru";

    public static IReadOnlyList<string> Supported { get; } = [English, Russian];

    public string Culture { get; private set; } = English;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? Changed;

    public string this[string key] => Get(key);

    public string Get(string key)
    {
        if (Culture == Russian && Strings.Ru.TryGetValue(key, out var ru)) return ru;
        return Strings.EnglishTable.TryGetValue(key, out var en) ? en : key;
    }

    public string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>
    /// Translate a phrase by its English text rather than a key. Used for labels that
    /// originate outside the UI layer - plugin field names and help - where inventing
    /// keys would push localization into the plugin contract.
    /// </summary>
    public string? Translate(string? text)
    {
        if (text is null) return null;
        return Culture == Russian && Strings.RuPhrases.TryGetValue(text, out var ru) ? ru : text;
    }

    /// <summary>Apply a setting value: "en", "ru", or anything else for "follow Windows".</summary>
    public void Apply(string? setting)
    {
        var culture = Resolve(setting);
        if (culture == Culture) return;

        Culture = culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        Changed?.Invoke();
    }

    public static string Resolve(string? setting) => setting switch
    {
        Russian => Russian,
        English => English,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals(Russian, StringComparison.OrdinalIgnoreCase)
            ? Russian
            : English,
    };
}

/// <summary>Short-hand for code that builds user-facing text.</summary>
public static class L
{
    public static string T(string key) => Localizer.Instance.Get(key);
    public static string F(string key, params object?[] args) => Localizer.Instance.Format(key, args);
    public static string? P(string? phrase) => Localizer.Instance.Translate(phrase);
}

public static partial class Strings
{
    private static Dictionary<string, string>? _english;

    /// <summary>Every English string: the XAML table plus the code table.</summary>
    public static IReadOnlyDictionary<string, string> EnglishTable
    {
        get
        {
            if (_english is not null) return _english;
            var merged = new Dictionary<string, string>(XamlEn, StringComparer.Ordinal);
            foreach (var (k, v) in CodeEn) merged[k] = v;
            return _english = merged;
        }
    }
}
