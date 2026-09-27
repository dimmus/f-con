using FCon.Core.Storage;
using FCon.Core.Subscriptions;

namespace FCon.Core.Tests;

public sealed class BuiltInSubscriptionTests
{
    private static ProfileStore TempStore() =>
        new(Path.Combine(Path.GetTempPath(), $"fcon-tests-{Guid.NewGuid():N}.json"));

    [Fact]
    public void A_fresh_store_gets_every_shipped_subscription_once()
    {
        var store = TempStore();

        Assert.Equal(DefaultSubscriptions.All.Count, store.EnsureBuiltIn(DefaultSubscriptions.All));
        Assert.Equal(0, store.EnsureBuiltIn(DefaultSubscriptions.All));

        var subs = store.Subscriptions;
        Assert.Equal(DefaultSubscriptions.All.Count, subs.Count);
        Assert.All(subs, s => Assert.True(s.IsBuiltIn));
        Assert.All(subs, s => Assert.True(s.Enabled));
        Assert.Contains(subs, s => s.Url.EndsWith("BLACK_VLESS_RUS.txt"));
        Assert.Contains(subs, s => s.Url.EndsWith("BLACK_SS%2BAll_RUS.txt"));
    }

    [Fact]
    public void A_hand_added_copy_is_adopted_not_duplicated_and_gets_a_real_name()
    {
        var store = TempStore();

        // The user typed the same address with a literal plus and the bare host as name,
        // which is exactly what the Add button produces.
        var typed = "https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/refs/heads/main/BLACK_SS+All_RUS.txt";
        store.UpsertSubscription(new Subscription { Url = typed, Name = "raw.githubusercontent.com" });

        var added = store.EnsureBuiltIn(DefaultSubscriptions.All);

        Assert.Equal(1, added);
        Assert.Equal(2, store.Subscriptions.Count);

        var adopted = store.Subscriptions.Single(s => s.Url == typed);
        Assert.True(adopted.IsBuiltIn);
        Assert.Equal("Russia · Shadowsocks + others (igareck)", adopted.Name);
    }

    [Fact]
    public void A_user_chosen_name_is_kept_when_adopting()
    {
        var store = TempStore();
        store.UpsertSubscription(new Subscription
        {
            Url = DefaultSubscriptions.All[0].Url,
            Name = "My VLESS list",
        });

        store.EnsureBuiltIn(DefaultSubscriptions.All);

        Assert.Equal("My VLESS list", store.Subscriptions.Single(s => s.Url == DefaultSubscriptions.All[0].Url).Name);
    }

    [Fact]
    public void Deactivating_a_built_in_is_allowed_and_survives_reseeding()
    {
        var store = TempStore();
        store.EnsureBuiltIn(DefaultSubscriptions.All);
        var first = store.Subscriptions[0];

        store.SetSubscriptionEnabled(first.Id, false);
        store.EnsureBuiltIn(DefaultSubscriptions.All);

        Assert.False(store.Subscriptions.Single(s => s.Id == first.Id).Enabled);
    }

    [Theory]
    [InlineData("https://a.example/x%2By.txt", "https://a.example/x+y.txt", true)]
    [InlineData("https://A.example/X.txt", "https://a.example/x.txt", true)]
    [InlineData("https://a.example/x.txt/", "https://a.example/x.txt", true)]
    [InlineData("https://a.example/x.txt", "https://a.example/y.txt", false)]
    public void Url_comparison_ignores_encoding_case_and_trailing_slash(string a, string b, bool same)
    {
        Assert.Equal(same, DefaultSubscriptions.SameUrl(a, b));
    }
}
