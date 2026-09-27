using PwVault.Core.Bridge;
using PwVault.Core.Icons;

namespace PwVault.Core.Tests;

public class IconCacheTests
{
    private const string Password = "correct horse battery staple";
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    [Fact]
    public void SaveLoad_RoundTrips_AndHidesHostNames()
    {
        using var dir = new TempDir();
        var vaultPath = dir.File("vault.pwv");
        using var vault = Vault.Create(vaultPath, Password, TestKdf.Fast());
        var cache = IconCache.Empty();
        cache.SetFromBrowser("secret-bank.example", Png, Now);
        cache.SetFromSite("nothing.example", null, Now);
        cache.Save(vault, IconCache.PathFor(vaultPath));

        Assert.DoesNotContain("secret-bank", File.ReadAllText(IconCache.PathFor(vaultPath)));

        var loaded = IconCache.Load(vault, IconCache.PathFor(vaultPath));
        Assert.Equal(Png, loaded.Get("secret-bank.example")!.Data);
        Assert.Equal(IconSource.Browser, loaded.Get("secret-bank.example")!.Source);
        Assert.True(loaded.Get("nothing.example")!.IsMissing);
    }

    [Fact]
    public void Load_FromOtherVaultOrGarbage_IsEmpty()
    {
        using var dir = new TempDir();
        using var a = Vault.Create(dir.File("a.pwv"), Password, TestKdf.Fast());
        using var b = Vault.Create(dir.File("b.pwv"), Password, TestKdf.Fast());
        var cache = IconCache.Empty();
        cache.SetFromBrowser("x.example", Png, Now);
        cache.Save(a, dir.File("icons"));

        Assert.Equal(0, IconCache.Load(b, dir.File("icons")).Count);
        File.WriteAllText(dir.File("garbage"), "{not json");
        Assert.Equal(0, IconCache.Load(a, dir.File("garbage")).Count);
        Assert.Equal(0, IconCache.Load(a, dir.File("missing")).Count);
    }

    [Fact]
    public void BrowserIcon_WinsOverSiteIcon()
    {
        var cache = IconCache.Empty();
        cache.SetFromBrowser("a.example", Png, Now);
        Assert.False(cache.SetFromSite("a.example", [9, 9], Now));
        Assert.Equal(Png, cache.Get("a.example")!.Data);
        Assert.False(cache.NeedsSiteFetch("a.example", Now.AddYears(1)));

        cache.SetFromSite("b.example", [9, 9], Now);
        cache.SetFromBrowser("b.example", Png, Now);
        Assert.Equal(Png, cache.Get("b.example")!.Data);
    }

    [Fact]
    public void NeedsSiteFetch_RespectsRefreshAndRetryIntervals()
    {
        var cache = IconCache.Empty();
        Assert.True(cache.NeedsSiteFetch("new.example", Now));

        cache.SetFromSite("ok.example", Png, Now);
        Assert.False(cache.NeedsSiteFetch("ok.example", Now.AddDays(29)));
        Assert.True(cache.NeedsSiteFetch("ok.example", Now.AddDays(30)));

        cache.SetFromSite("none.example", null, Now);
        Assert.False(cache.NeedsSiteFetch("none.example", Now.AddDays(6)));
        Assert.True(cache.NeedsSiteFetch("none.example", Now.AddDays(7)));
    }

    [Fact]
    public void RemoveAllExcept_DropsUnusedHosts()
    {
        var cache = IconCache.Empty();
        cache.SetFromBrowser("keep.example", Png, Now);
        cache.SetFromBrowser("gone.example", Png, Now);
        cache.RemoveAllExcept(new HashSet<string> { "keep.example" });
        Assert.NotNull(cache.Get("keep.example"));
        Assert.Null(cache.Get("gone.example"));
    }

    [Fact]
    public void RejectsOversizedIcons() =>
        Assert.Throws<ArgumentException>(() => IconCache.Empty().SetFromBrowser("a", new byte[IconCache.MaxIconBytes + 1], Now));

    [Theory]
    [InlineData("https://www.Example.com/login", "example.com")]
    [InlineData("example.com", "example.com")]
    [InlineData("http://login.example.com:8080/", "login.example.com")]
    [InlineData("", null)]
    [InlineData("ftp://example.com", null)]
    public void HostKey(string url, string? expected) => Assert.Equal(expected, UrlMatcher.HostKey(url));
}

public class BridgeIconTests
{
    private static readonly VaultEntry Site = UrlMatcherTests.E("Site", "https://example.com");
    private static readonly string PngDataUrl = "data:image/png;base64," + Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47]);

    [Fact]
    public void Icon_IsStoredUnderSavedHost()
    {
        var stored = new List<(string Host, byte[] Data)>();
        var r = BridgeHandler.Handle(new BridgeRequest { Type = "icon", Url = "https://login.example.com/", Icon = PngDataUrl },
            [Site], (h, d) => stored.Add((h, d)));
        Assert.True(r.Ok);
        var (host, data) = Assert.Single(stored);
        Assert.Equal("example.com", host);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], data);
    }

    [Fact]
    public void Icon_ForSiteWithoutEntry_IsIgnored()
    {
        var stored = 0;
        var r = BridgeHandler.Handle(new BridgeRequest { Type = "icon", Url = "https://unrelated.test/", Icon = PngDataUrl },
            [Site], (_, _) => stored++);
        Assert.Equal(BridgeResponse.ErrorNoMatch, r.Error);
        Assert.Equal(0, stored);
    }

    [Fact]
    public void Icon_WhenLocked_IsRejected() =>
        Assert.Equal(BridgeResponse.ErrorLocked,
            BridgeHandler.Handle(new BridgeRequest { Type = "icon", Url = "https://example.com/", Icon = PngDataUrl }, null, (_, _) => { }).Error);

    [Theory]
    [InlineData("data:image/svg+xml;base64,PHN2Zy8+")]      // SVG は受け取らない
    [InlineData("data:image/png,rawtext")]                  // base64 でない
    [InlineData("data:image/png;base64,!!!")]               // 壊れた base64
    [InlineData("https://example.com/favicon.ico")]         // data: URL でない
    [InlineData(null)]
    public void Icon_InvalidData_IsRejected(string? icon)
    {
        var stored = 0;
        var r = BridgeHandler.Handle(new BridgeRequest { Type = "icon", Url = "https://example.com/", Icon = icon }, [Site], (_, _) => stored++);
        Assert.Equal(BridgeResponse.ErrorBadRequest, r.Error);
        Assert.Equal(0, stored);
    }
}
