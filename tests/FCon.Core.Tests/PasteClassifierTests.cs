using FCon.Core.Import;

namespace FCon.Core.Tests;

public sealed class PasteClassifierTests
{
    [Theory]
    [InlineData("https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/refs/heads/main/BLACK_VLESS_RUS.txt")]
    [InlineData("  http://example.com/sub?token=abc  ")]
    [InlineData("https://example.com/sub\n")]
    public void A_single_http_address_is_a_subscription(string text)
    {
        Assert.Equal(PasteKind.SubscriptionUrl, PasteClassifier.Classify(text, out var url));
        Assert.StartsWith("http", url);
        Assert.DoesNotContain("\n", url);
    }

    [Theory]
    [InlineData("vless://Goose_86166638@212.43.157.97:443?type=grpc&security=reality&fp=firefox&pbk=_l-yl8i4F3eDa-YzxrDxMwdPHfDawM56hVHQYTnhEkI&sni=wechat.com&sid=ffffffffff&spx=%2F#Goose_86166638-Нидерланды NEWS")]
    [InlineData("ss://YWVzLTI1Ni1nY206cGFzc3dvcmQxMjNAc3MyLmV4YW1wbGUuY29tOjg0NDM#Legacy")]
    [InlineData("vless://a@b:1#x\nvless://c@d:2#y")]
    [InlineData("https://one.example/sub\nhttps://two.example/sub")]
    [InlineData("dmxlc3M6Ly9hQGI6MSN4")]
    public void Anything_else_goes_to_the_link_importer(string text)
    {
        Assert.Equal(PasteKind.Links, PasteClassifier.Classify(text, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n ")]
    public void Blank_clipboard_is_empty(string? text)
    {
        Assert.Equal(PasteKind.Empty, PasteClassifier.Classify(text, out _));
    }

    [Fact]
    public void The_pasted_vless_link_parses_into_a_reality_node()
    {
        var registry = Core.Plugins.PluginRegistry.CreateDefault(loadExternal: false);
        var node = new LinkImporter(registry).Import(
            "vless://Goose_86166638@212.43.157.97:443?type=grpc&security=reality&fp=firefox&pbk=_l-yl8i4F3eDa-YzxrDxMwdPHfDawM56hVHQYTnhEkI&sni=wechat.com&sid=ffffffffff&spx=%2F#Goose_86166638-Нидерланды NEWS")
            .Nodes.Single();

        Assert.Equal("vless", node.Protocol);
        Assert.Equal("212.43.157.97:443", node.Endpoint);
        Assert.Equal(Abstractions.Model.TransportKind.Grpc, node.Transport.Kind);
        Assert.Equal(Abstractions.Model.SecurityKind.Reality, node.Security.Kind);
        Assert.Equal("wechat.com", node.Security.ServerName);
        Assert.Equal("Goose_86166638-Нидерланды NEWS", node.Remark);
    }
}
