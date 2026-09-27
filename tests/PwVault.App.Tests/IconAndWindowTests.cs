using System.Text;
using Avalonia.Headless.XUnit;
using PwVault.App.Services;
using PwVault.App.ViewModels;
using PwVault.Core;
using PwVault.Core.Bridge;
using PwVault.Core.Crypto;
using PwVault.Core.Icons;

namespace PwVault.App.Tests;

public class FaviconFetcherTests
{
    private static readonly byte[] Png = FakeIconServer.Png;
    private static bool IsPng(byte[] d) => d.Length > 8 && d[0] == 0x89 && d[1] == 0x50;

    private static FakeIconServer Server(params (string Url, string Type, byte[] Body)[] pages)
    {
        var s = new FakeIconServer();
        foreach (var p in pages) s.Pages[p.Url] = (p.Type, p.Body);
        return s;
    }

    private static byte[] Html(string body) => Encoding.UTF8.GetBytes($"<html><head>{body}</head></html>");

    [Fact]
    public async Task UsesLinkTag_PreferringSizeNear64_AndSkippingSvg()
    {
        var server = Server(
            ("https://example.com/", "text/html", Html("""
                <link rel="icon" type="image/svg+xml" href="/icon.svg">
                <link rel="icon" sizes="16x16" href="/small.png">
                <LINK REL='apple-touch-icon' href='/touch.png'>
                <link href="/assets/icon-64.png?v=2" rel="shortcut icon" sizes="64x64">
                """)),
            ("https://example.com/assets/icon-64.png?v=2", "image/png", Png),
            ("https://example.com/small.png", "image/png", [1, 2, 3]));

        var data = await new FaviconFetcher(server).FetchAsync(new Uri("https://example.com/login"), IsPng, TestContext.Current.CancellationToken);
        Assert.Equal(Png, data);
        Assert.DoesNotContain(server.Requests, u => u.AbsolutePath.EndsWith(".svg"));
    }

    [Fact]
    public async Task FallsBackToFaviconIco()
    {
        var server = Server(
            ("https://example.com/", "text/html", Html("<title>no icon</title>")),
            ("https://example.com/favicon.ico", "image/x-icon", Png));
        Assert.Equal(Png, await new FaviconFetcher(server).FetchAsync(new Uri("https://example.com/"), IsPng, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RejectsNonImages_Svg_AndOversized()
    {
        var server = Server(
            ("https://a.example/", "text/html", Html("""<link rel="icon" href="/fake.png"><link rel="icon" href="/big.png">""")),
            ("https://a.example/fake.png", "image/png", Encoding.UTF8.GetBytes("not an image")),
            ("https://a.example/big.png", "image/png", new byte[IconCache.MaxIconBytes + 1]),
            ("https://a.example/favicon.ico", "image/svg+xml", Encoding.UTF8.GetBytes("<svg/>")));
        Assert.Null(await new FaviconFetcher(server).FetchAsync(new Uri("https://a.example/"), IsPng, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ParsesRelativeAndAbsoluteLinks()
    {
        var links = FaviconFetcher.ParseIconLinks(
            """<link rel="icon" href="//cdn.example/i.png"><link rel="icon" href="data:image/png;base64,AA"><link rel="stylesheet" href="/x.css">""",
            new Uri("https://example.com/path/"));
        Assert.Equal([new Uri("https://cdn.example/i.png")], links);
    }
}

public class IconAndWindowTests
{
    private const string Master = "correct horse battery staple";

    private static void CreateVault(string path)
    {
        using var v = Vault.Create(path, Master, KdfParameters.CreateNew(KdfParameters.MinRecommendedMemoryKiB, 2));
        v.AddEntry(new EntryData { Title = "Example", Username = "alice", Password = "k#8Vq!2mZr$9Lx@4", Url = "https://www.example.com/login" });
        v.AddEntry(new EntryData { Title = "Browser only", Username = "bob", Password = "k#8Vq!2mZr$9Lx@5", Url = "https://browser.example" });
        v.AddEntry(new EntryData { Title = "No url", Username = "carol", Password = "k#8Vq!2mZr$9Lx@6" });
        v.Save(5);
    }

    private static async Task<VaultViewModel> Unlock(Harness h)
    {
        h.Main.ShowUnlock(h.VaultPath);
        var unlock = h.Page<UnlockViewModel>();
        unlock.Password = Master;
        await unlock.UnlockCommand.ExecuteAsync(null);
        return h.Page<VaultViewModel>();
    }

    [AvaloniaFact]
    public async Task SiteIcons_AreFetched_Cached_AndReusedAfterUnlock()
    {
        using var h = new Harness();
        CreateVault(h.VaultPath);
        h.Icons.Pages["https://www.example.com/favicon.ico"] = ("image/x-icon", FakeIconServer.Png);

        var vm = await Unlock(h);
        await Harness.WaitFor(() => vm.Items.Single(i => i.Title == "Example").HasIcon);
        Assert.False(vm.Items.Single(i => i.Title == "No url").HasIcon);
        h.Screenshot("14-list-with-icons");

        // ロックすると暗号化キャッシュに保存され、ホスト名は平文で残らない
        h.Main.LockCommand.Execute(null);
        var cacheFile = IconCache.PathFor(h.VaultPath);
        Assert.True(File.Exists(cacheFile));
        Assert.DoesNotContain("example.com", File.ReadAllText(cacheFile));

        // 次のアンロックではキャッシュから表示し、同じサイトには取りに行かない
        var requestsBefore = h.Icons.Requests.Count(u => u.Host == "www.example.com");
        vm = await Unlock(h);
        Assert.True(vm.Items.Single(i => i.Title == "Example").HasIcon);
        Harness.Pump();
        Assert.Equal(requestsBefore, h.Icons.Requests.Count(u => u.Host == "www.example.com"));
    }

    [AvaloniaFact]
    public async Task SiteIconFetch_CanBeTurnedOff()
    {
        using var h = new Harness(s => s.FetchSiteIcons = false);
        CreateVault(h.VaultPath);
        h.Icons.Pages["https://www.example.com/favicon.ico"] = ("image/x-icon", FakeIconServer.Png);

        var vm = await Unlock(h);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Harness.Pump();
        Assert.Empty(h.Icons.Requests);          // 一切通信しない
        Assert.All(vm.Items, i => Assert.False(i.HasIcon));

        // 設定画面でオンにすると取りに行く
        vm.ShowSettingsCommand.Execute(null);
        ((SettingsViewModel)vm.SubPage!).FetchSiteIcons = true;
        vm.CloseSubPageCommand.Execute(null);
        await Harness.WaitFor(() => vm.Items.Single(i => i.Title == "Example").HasIcon);
    }

    [AvaloniaFact]
    public async Task BrowserIcon_ArrivesViaBridge_AndWinsOverSite()
    {
        using var h = new Harness(s => s.FetchSiteIcons = false);
        CreateVault(h.VaultPath);
        var vm = await Unlock(h);

        var dataUrl = "data:image/png;base64," + Convert.ToBase64String(FakeIconServer.Png);
        var r = await h.Main.HandleBridgeRequestAsync(new BridgeRequest { Type = "icon", Url = "https://login.browser.example/", Icon = dataUrl });
        Assert.True(r.Ok);
        await Harness.WaitFor(() => vm.Items.Single(i => i.Title == "Browser only").HasIcon);

        // 画像として読めないものは保存しない
        var bad = "data:image/png;base64," + Convert.ToBase64String("not an image"u8.ToArray());
        await h.Main.HandleBridgeRequestAsync(new BridgeRequest { Type = "icon", Url = "https://www.example.com/", Icon = bad });
        Harness.Pump();
        Assert.False(vm.Items.Single(i => i.Title == "Example").HasIcon);
    }

    [AvaloniaFact]
    public async Task DoubleClick_OpensSite_OnlyWhenEnabled()
    {
        using var h = new Harness(s => s.FetchSiteIcons = false);
        CreateVault(h.VaultPath);
        var vm = await Unlock(h);

        vm.SelectedItem = vm.Items.Single(i => i.Title == "Example");
        vm.OnItemDoubleClicked();
        Assert.Equal(new Uri("https://www.example.com/login"), Assert.Single(h.OpenedUrls));

        vm.SelectedItem = vm.Items.Single(i => i.Title == "No url");
        vm.OnItemDoubleClicked();
        Assert.Single(h.OpenedUrls);

        vm.ShowSettingsCommand.Execute(null);
        ((SettingsViewModel)vm.SubPage!).DoubleClickOpensUrl = false;
        vm.CloseSubPageCommand.Execute(null);
        Assert.False(h.Main.Settings.DoubleClickOpensUrl);
        Assert.False(new AppSettingsStore(Path.Combine(h.Dir, "settings.json")).Load().DoubleClickOpensUrl); // 保存されている

        vm.SelectedItem = vm.Items.Single(i => i.Title == "Example");
        vm.OnItemDoubleClicked();
        Assert.Single(h.OpenedUrls);
    }

    [AvaloniaFact]
    public void CloseButton_HidesToTray_WhenEnabled()
    {
        using var h = new Harness();
        Assert.True(h.Main.Settings.CloseToTray); // 既定でオン

        h.Window.Close();
        Harness.Pump();
        Assert.False(h.Window.IsVisible);          // 隠れただけで閉じていない
        h.Window.Show();
        Assert.True(h.Window.IsVisible);

        h.Main.Settings.CloseToTray = false;
        var closed = false;
        h.Window.Closed += (_, _) => closed = true;
        h.Window.Close();
        Assert.True(closed);
    }
}
