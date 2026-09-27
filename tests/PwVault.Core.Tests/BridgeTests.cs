using PwVault.Core.Bridge;

namespace PwVault.Core.Tests;

public class UrlMatcherTests
{
    [Theory]
    [InlineData("https://example.com/login", "https://example.com", UrlMatch.Exact)]
    [InlineData("https://www.example.com/", "https://example.com", UrlMatch.Exact)]
    [InlineData("https://example.com/", "https://www.example.com/path", UrlMatch.Exact)]
    [InlineData("https://EXAMPLE.com/", "example.com", UrlMatch.Exact)]              // スキーム省略は https
    [InlineData("https://login.example.com/", "https://example.com", UrlMatch.Subdomain)]
    [InlineData("https://a.b.example.com/", "https://example.com", UrlMatch.Subdomain)]
    [InlineData("https://example.com/", "http://example.com", UrlMatch.Exact)]       // http 保存 → https ページは可
    [InlineData("http://example.com/", "https://example.com", UrlMatch.None)]        // https 保存 → http ページは不可
    [InlineData("https://example.com.evil.test/", "https://example.com", UrlMatch.None)]
    [InlineData("https://evilexample.com/", "https://example.com", UrlMatch.None)]
    [InlineData("https://example.com/", "https://login.example.com", UrlMatch.None)] // 親ドメインには出さない
    [InlineData("https://mail.example.com/", "https://login.example.com", UrlMatch.None)]
    [InlineData("https://example.com:8443/", "https://example.com", UrlMatch.None)]
    [InlineData("https://example.com:8443/", "https://example.com:8443", UrlMatch.Exact)]
    [InlineData("https://example.com/", "https://example.com:8443", UrlMatch.None)]
    [InlineData("http://192.168.0.1/", "http://192.168.0.1", UrlMatch.Exact)]
    [InlineData("http://10.192.168.0.1/", "http://192.168.0.1", UrlMatch.None)]
    [InlineData("http://localhost:3000/", "http://localhost:3000", UrlMatch.Exact)]
    [InlineData("http://app.localhost:3000/", "http://localhost:3000", UrlMatch.None)]
    [InlineData("https://ドメイン.example/", "https://xn--eckwd4c7c.example", UrlMatch.Exact)] // IDN
    [InlineData("file:///C:/x.html", "https://example.com", UrlMatch.None)]
    [InlineData("chrome://settings", "chrome://settings", UrlMatch.None)]
    [InlineData("https://example.com/", "", UrlMatch.None)]
    [InlineData("https://example.com/", "javascript:alert(1)", UrlMatch.None)]
    public void Match(string page, string saved, UrlMatch expected) =>
        Assert.Equal(expected, UrlMatcher.Match(page, saved));

    [Fact]
    public void FindMatches_OrdersExactFirst_AndSkipsTrash()
    {
        var sub = E("B sub", "https://example.com");
        var exact = E("Z exact", "https://login.example.com");
        var fav = E("Y fav sub", "https://example.com", favorite: true);
        var trashed = E("trashed", "https://login.example.com", trashed: true);
        var other = E("other", "https://other.test");

        var result = UrlMatcher.FindMatches([sub, exact, fav, trashed, other], "https://login.example.com/signin");
        Assert.Equal(["Z exact", "Y fav sub", "B sub"], result.Select(e => e.Data.Title));
    }

    internal static VaultEntry E(string title, string url, bool favorite = false, bool trashed = false) =>
        new(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, new EntryData
        {
            Title = title, Url = url, Username = title + "-user", Password = title + "-pw",
            Favorite = favorite, TrashedAt = trashed ? DateTimeOffset.UtcNow : null,
        });
}

public class BridgeHandlerTests
{
    private static readonly VaultEntry Site = UrlMatcherTests.E("Site", "https://example.com");
    private static readonly VaultEntry Other = UrlMatcherTests.E("Other", "https://other.test");
    private static readonly IReadOnlyList<VaultEntry> Entries = [Site, Other];

    [Fact]
    public void Status_ReportsLockState()
    {
        Assert.True(BridgeHandler.Handle(new BridgeRequest { Type = "status" }, Entries).Unlocked);
        Assert.False(BridgeHandler.Handle(new BridgeRequest { Type = "status" }, null).Unlocked);
    }

    [Fact]
    public void List_WhenLocked_ReturnsLocked()
    {
        var r = BridgeHandler.Handle(new BridgeRequest { Type = "list", Url = "https://example.com" }, null);
        Assert.False(r.Ok);
        Assert.Equal(BridgeResponse.ErrorLocked, r.Error);
    }

    [Fact]
    public void List_ReturnsMatches_WithoutPasswords()
    {
        var r = BridgeHandler.Handle(new BridgeRequest { Type = "list", Url = "https://www.example.com/login" }, Entries);
        Assert.True(r.Ok);
        var e = Assert.Single(r.Entries!);
        Assert.Equal(Site.Id.ToString("D"), e.Id);
        Assert.Null(r.Password);
        Assert.DoesNotContain("Site-pw", System.Text.Encoding.UTF8.GetString(BridgeMessage.Serialize(r)));
    }

    [Fact]
    public void Fill_ReturnsCredentials_OnlyForMatchingUrl()
    {
        var ok = BridgeHandler.Handle(new BridgeRequest { Type = "fill", Url = "https://example.com/", Id = Site.Id.ToString() }, Entries);
        Assert.True(ok.Ok);
        Assert.Equal(("Site-user", "Site-pw"), (ok.Username, ok.Password));

        // 他サイトのエントリ ID を指定しても渡さない（ページが移動した・悪意ある要求）
        var denied = BridgeHandler.Handle(new BridgeRequest { Type = "fill", Url = "https://example.com/", Id = Other.Id.ToString() }, Entries);
        Assert.False(denied.Ok);
        Assert.Equal(BridgeResponse.ErrorNoMatch, denied.Error);
        Assert.Null(denied.Password);
    }

    [Theory]
    [InlineData(null, "https://example.com")]
    [InlineData("unknown", "https://example.com")]
    [InlineData("list", null)]
    [InlineData("fill", "https://example.com")] // ID なし
    public void BadRequests_AreRejected(string? type, string? url) =>
        Assert.Equal(BridgeResponse.ErrorBadRequest,
            BridgeHandler.Handle(new BridgeRequest { Type = type, Url = url }, Entries).Error);

    [Fact]
    public async Task Framing_RoundTrips_AndRejectsOversize()
    {
        var ct = TestContext.Current.CancellationToken;
        var ms = new MemoryStream();
        await BridgeMessage.WriteFrameAsync(ms, BridgeMessage.Serialize(new BridgeRequest { Type = "list", Url = "https://a.test" }), ct);
        ms.Position = 0;
        var body = await BridgeMessage.ReadFrameAsync(ms, BridgeMessage.MaxRequestBytes, ct);
        Assert.Equal("list", BridgeMessage.ParseRequest(body)!.Type);
        Assert.Null(await BridgeMessage.ReadFrameAsync(ms, BridgeMessage.MaxRequestBytes, ct)); // 終端

        var big = new MemoryStream([0x00, 0x00, 0x10, 0x00]); // 1 MB
        await Assert.ThrowsAsync<InvalidDataException>(() => BridgeMessage.ReadFrameAsync(big, BridgeMessage.MaxRequestBytes, ct));

        Assert.Null(BridgeMessage.ParseRequest("not json"u8));
    }
}
