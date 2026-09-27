using System.Text.RegularExpressions;
using FCon.Core.Localization;

namespace FCon.Core.Tests;

public sealed class LocalizationTests
{
    private static IEnumerable<string> Placeholders(string s) =>
        Regex.Matches(s, @"\{(\d+)").Select(m => m.Groups[1].Value).Distinct().Order();

    [Fact]
    public void Every_english_string_has_a_russian_translation()
    {
        var missing = Strings.EnglishTable.Keys.Where(k => !Strings.Ru.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "Missing in Russian: " + string.Join(", ", missing));
    }

    [Fact]
    public void Russian_has_no_keys_english_lacks()
    {
        var extra = Strings.Ru.Keys.Where(k => !Strings.EnglishTable.ContainsKey(k)).ToList();
        Assert.True(extra.Count == 0, "Unknown Russian keys: " + string.Join(", ", extra));
    }

    [Fact]
    public void Placeholders_match_between_languages()
    {
        var mismatched = Strings.EnglishTable
            .Where(kv => Strings.Ru.TryGetValue(kv.Key, out var ru) && !Placeholders(kv.Value).SequenceEqual(Placeholders(ru)))
            .Select(kv => kv.Key)
            .ToList();
        Assert.True(mismatched.Count == 0, "Placeholder mismatch: " + string.Join(", ", mismatched));
    }

    [Fact]
    public void No_translation_is_blank_and_none_still_says_fcon()
    {
        Assert.All(Strings.Ru.Values, v => Assert.False(string.IsNullOrWhiteSpace(v)));
        Assert.All(Strings.EnglishTable.Values, v => Assert.DoesNotContain("FCon", v));
        Assert.All(Strings.Ru.Values, v => Assert.DoesNotContain("FCon", v));
        Assert.All(Strings.RuPhrases, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value)));
    }

    [Fact]
    public void Unknown_keys_fall_back_to_the_key_and_missing_phrases_pass_through()
    {
        var loc = Localizer.Instance;
        loc.Apply("en");
        Assert.Equal("No_such_key", loc["No_such_key"]);
        Assert.Equal("Password", loc.Translate("Password"));
        Assert.Null(loc.Translate(null));

        loc.Apply("ru");
        Assert.Equal("Пароль", loc.Translate("Password"));
        Assert.Equal("Something unlisted", loc.Translate("Something unlisted"));
        loc.Apply("en");
    }

    [Fact]
    public void Switching_language_raises_change_and_swaps_strings()
    {
        var loc = Localizer.Instance;
        loc.Apply("en");
        var raised = 0;
        Action handler = () => raised++;
        loc.Changed += handler;
        try
        {
            Assert.Equal("Connect", loc["Connect"]);
            loc.Apply("ru");
            Assert.Equal("Подключить", loc["Connect"]);
            Assert.Equal("Проверка 3/10", loc.Format("Testing", 3, 10));
            loc.Apply("ru");                 // no change, no event
            Assert.Equal(1, raised);
        }
        finally
        {
            loc.Changed -= handler;
            loc.Apply("en");
        }
    }

    [Theory]
    [InlineData("ru", "ru")]
    [InlineData("en", "en")]
    [InlineData("RU", "en")]   // the setting is exact; anything else means "follow Windows"
    public void Explicit_settings_resolve_exactly(string setting, string expectedUnlessWindowsIsRussian)
    {
        var resolved = Localizer.Resolve(setting);
        if (setting is "ru" or "en") Assert.Equal(expectedUnlessWindowsIsRussian, resolved);
        else Assert.Contains(resolved, Localizer.Supported);
    }
}
