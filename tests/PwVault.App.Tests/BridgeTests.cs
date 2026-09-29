using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Microsoft.Win32;
using PwVault.App.Bridge;
using PwVault.App.ViewModels;
using PwVault.Core;
using PwVault.Core.Bridge;
using PwVault.Core.Crypto;

namespace PwVault.App.Tests;

/// <summary>ブラウザ連携: 中継（NativeHost）→ 名前付きパイプ → PwVault 本体 の往復と、登録処理。</summary>
public class BridgeTests
{
    private const string Master = "correct horse battery staple";

    private static string TestPipe() => Harness.TestPipe();

    /// <summary>ブラウザが中継を起動したときと同じく、標準入力に 1 メッセージを入れて中継を 1 回動かす。</summary>
    private static async Task<BridgeResponse> AskViaNativeHost(string pipeName, BridgeRequest request)
    {
        var stdin = new MemoryStream();
        await BridgeMessage.WriteFrameAsync(stdin, BridgeMessage.Serialize(request));
        stdin.Position = 0;
        var stdout = new MemoryStream();

        // 中継は別スレッドで動かす（UI スレッドで待つと、本体側の UI スレッド処理と行き詰まるため）
        var run = Task.Run(() => NativeHost.RunAsync(stdin, stdout, pipeName, TimeSpan.FromSeconds(2)));
        // 呼び出し元は UI スレッド（[AvaloniaFact]）。本体側の処理が UI スレッドに投げられるので回し続ける
        while (!run.IsCompleted)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.Equal(0, await run);

        stdout.Position = 0;
        return BridgeMessage.ParseResponse(await BridgeMessage.ReadFrameAsync(stdout, BridgeMessage.MaxResponseBytes))!;
    }

    [Fact]
    public void DetectsBrowserLaunchArguments()
    {
        Assert.True(NativeHost.IsNativeMessagingLaunch(["chrome-extension://gfjoammhdpkemdcngfdmipagkehaljgl/", "--parent-window=0"]));
        Assert.True(NativeHost.IsNativeMessagingLaunch([@"C:\x\com.pwvault.native.firefox.json", "pwvault-autofill@pwvault.local"]));
        Assert.False(NativeHost.IsNativeMessagingLaunch([]));
        Assert.False(NativeHost.IsNativeMessagingLaunch(["--something"]));
    }

    [AvaloniaFact]
    public async Task NativeHost_WhenAppNotRunning_ReportsNotRunning()
    {
        var r = await AskViaNativeHost(TestPipe(), new BridgeRequest { Type = "status" });
        Assert.False(r.Ok);
        Assert.Equal(BridgeResponse.ErrorNotRunning, r.Error);
    }

    [AvaloniaFact]
    public async Task Server_RejectsUntrustedClient()
    {
        var pipe = TestPipe();
        await using var server = new BridgeServer(_ => Task.FromResult(new BridgeResponse { Ok = true }), pipe, isTrustedClient: _ => false);
        var r = await AskViaNativeHost(pipe, new BridgeRequest { Type = "status" });
        Assert.Equal(BridgeResponse.ErrorNotRunning, r.Error); // 応答せずに切られる
    }

    [AvaloniaFact]
    public async Task Server_Restart_DoesNotLeaveDeadPipe()
    {
        // 止めた窓口の待ち受けパイプが残っていると、そこに繋がった中継が応答待ちで固まる
        var pipe = TestPipe();
        await (new BridgeServer(_ => Task.FromResult(new BridgeResponse { Ok = true }), pipe)).DisposeAsync();
        await using var server = new BridgeServer(_ => Task.FromResult(new BridgeResponse { Ok = true, Unlocked = true }), pipe);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 3; i++)
            Assert.True((await AskViaNativeHost(pipe, new BridgeRequest { Type = "status" })).Ok);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), sw.Elapsed.ToString());
    }

    [AvaloniaFact]
    public async Task Server_DefaultValidator_AcceptsSameExecutable()
    {
        // テストでは中継も同じプロセス（同じ exe）なので、既定の検査（接続元が自分と同じ exe か）を通る
        var pipe = TestPipe();
        await using var server = new BridgeServer(_ => Task.FromResult(new BridgeResponse { Ok = true, Unlocked = true }), pipe);
        var r = await AskViaNativeHost(pipe, new BridgeRequest { Type = "status" });
        Assert.True(r.Ok);
    }

    [AvaloniaFact]
    public async Task EndToEnd_LockedThenUnlocked_ListAndFill()
    {
        using var h = new Harness();
        using (var v = Vault.Create(h.VaultPath, Master, KdfParameters.CreateNew(KdfParameters.MinRecommendedMemoryKiB, 2)))
        {
            v.AddEntry(new EntryData { Title = "Example", Username = "alice", Password = "s3cret!", Url = "https://example.com" });
            v.AddEntry(new EntryData { Title = "Other", Username = "bob", Password = "other!", Url = "https://other.test" });
            v.Save(5);
        }
        h.Main.ShowUnlock(h.VaultPath);
        h.Main.EnableBrowserIntegration();
        Assert.True(h.Main.IsBridgeRunning);

        // ロック中
        var locked = await AskViaNativeHost(h.PipeName, new BridgeRequest { Type = "list", Url = "https://example.com/login" });
        Assert.Equal(BridgeResponse.ErrorLocked, locked.Error);

        // アンロック後
        var unlock = h.Page<UnlockViewModel>();
        unlock.Password = Master;
        await unlock.UnlockCommand.ExecuteAsync(null);

        var vm = h.Page<VaultViewModel>();
        vm.ShowSettingsCommand.Execute(null);
        var settings = Assert.IsType<SettingsViewModel>(vm.SubPage);
        Assert.True(settings.BrowserEnabled);
        h.Window.Height = 1250; // 設定画面の「ブラウザ連携」欄まで写す
        settings.ShowCategory(SettingsCategory.Browser);
        h.Screenshot("13-browser-settings");
        h.Window.Height = 720;
        vm.CloseSubPageCommand.Execute(null);

        var list = await AskViaNativeHost(h.PipeName, new BridgeRequest { Type = "list", Url = "https://www.example.com/login" });
        Assert.True(list.Ok);
        // 拡張が「自分は古いか」を判断できるよう、同梱の拡張の版を返す（ロック中も同じ）
        Assert.Equal(BrowserIntegration.BundledExtensionVersion, list.ExtensionVersion);
        Assert.Equal(BrowserIntegration.BundledExtensionVersion, locked.ExtensionVersion);
        var entry = Assert.Single(list.Entries!);
        Assert.Equal(("Example", "alice"), (entry.Title, entry.Username));

        var fill = await AskViaNativeHost(h.PipeName, new BridgeRequest { Type = "fill", Url = "https://example.com/login", Id = entry.Id });
        Assert.Equal(("alice", "s3cret!"), (fill.Username, fill.Password));

        // 別サイトの URL でこの ID を要求しても渡さない
        var wrongSite = await AskViaNativeHost(h.PipeName, new BridgeRequest { Type = "fill", Url = "https://other.test/", Id = entry.Id });
        Assert.Equal(BridgeResponse.ErrorNoMatch, wrongSite.Error);

        // ロックしたら再び拒否
        h.Main.LockCommand.Execute(null);
        var relocked = await AskViaNativeHost(h.PipeName, new BridgeRequest { Type = "fill", Url = "https://example.com/login", Id = entry.Id });
        Assert.Equal(BridgeResponse.ErrorLocked, relocked.Error);
        Assert.Null(relocked.Password);

        // 無効にしたら窓口も止まる
        h.Main.DisableBrowserIntegration();
        Assert.False(h.Main.IsBridgeRunning);
        var off = await AskViaNativeHost(h.PipeName, new BridgeRequest { Type = "status" });
        Assert.Equal(BridgeResponse.ErrorNotRunning, off.Error);
    }

    [AvaloniaFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Register_WritesManifests_RegistryAndExtension()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows 専用");
        using var h = new Harness();
        var integration = h.Integration;
        var exe = @"C:\Apps\PwVault\PwVault.exe";

        Assert.Equal(IntegrationStatus.NotRegistered, integration.GetStatus(exe));
        integration.Register(exe);
        Assert.Equal(IntegrationStatus.Registered, integration.GetStatus(exe));
        Assert.Equal(IntegrationStatus.PathMismatch, integration.GetStatus(@"D:\moved\PwVault.exe"));

        foreach (var (key, allowKey, allowed) in new[]
                 {
                     (@"Google\Chrome", "allowed_origins", $"chrome-extension://{NativeHost.ChromiumExtensionId}/"),
                     (@"Microsoft\Edge", "allowed_origins", $"chrome-extension://{NativeHost.ChromiumExtensionId}/"),
                     (@"Mozilla", "allowed_extensions", NativeHost.FirefoxExtensionId),
                 })
        {
            using var k = Registry.CurrentUser.OpenSubKey($@"{h.RegistryBase}\{key}\NativeMessagingHosts\{NativeHost.HostName}");
            var manifestPath = Assert.IsType<string>(k?.GetValue(null));
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
            Assert.Equal(NativeHost.HostName, manifest["name"]!.GetValue<string>());
            Assert.Equal(exe, manifest["path"]!.GetValue<string>());
            Assert.Equal("stdio", manifest["type"]!.GetValue<string>());
            Assert.Equal(allowed, manifest[allowKey]![0]!.GetValue<string>());
        }

        // 拡張機能はブラウザ別の manifest.json 付きで展開される
        var chromium = JsonNode.Parse(File.ReadAllText(Path.Combine(integration.ChromiumExtensionDir, "manifest.json")))!;
        Assert.Equal("background.js", chromium["background"]!["service_worker"]!.GetValue<string>());
        var firefox = JsonNode.Parse(File.ReadAllText(Path.Combine(integration.FirefoxExtensionDir, "manifest.json")))!;
        Assert.Equal(NativeHost.FirefoxExtensionId, firefox["browser_specific_settings"]!["gecko"]!["id"]!.GetValue<string>());
        foreach (var dir in new[] { integration.ChromiumExtensionDir, integration.FirefoxExtensionDir })
        {
            Assert.True(File.Exists(Path.Combine(dir, "background.js")));
            Assert.True(File.Exists(Path.Combine(dir, "icons", "128.png")));
            Assert.False(File.Exists(Path.Combine(dir, "manifest.chromium.json")));
        }

        // どれか 1 つのブラウザの登録が消えていたら「要修復」とみなす（起動時に登録し直す）
        Registry.CurrentUser.DeleteSubKeyTree($@"{h.RegistryBase}\Microsoft\Edge\NativeMessagingHosts\{NativeHost.HostName}");
        Assert.Equal(IntegrationStatus.PathMismatch, integration.GetStatus(exe));
        integration.Register(exe);
        Assert.Equal(IntegrationStatus.Registered, integration.GetStatus(exe));

        integration.Unregister();
        Assert.Equal(IntegrationStatus.NotRegistered, integration.GetStatus(exe));
    }

    [Fact]
    public void ExtensionManifests_HaveSameVersion_AsBundled()
    {
        // 本体は Chromium 用の版を「同梱の版」として拡張に伝えるので、Firefox 用も同じ版でなければならない
        var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "browser-extension");
        string Version(string file) => JsonNode.Parse(File.ReadAllText(Path.Combine(dir, file)))!["version"]!.GetValue<string>();
        Assert.NotNull(BrowserIntegration.BundledExtensionVersion);
        Assert.Equal(BrowserIntegration.BundledExtensionVersion, Version("manifest.chromium.json"));
        Assert.Equal(BrowserIntegration.BundledExtensionVersion, Version("manifest.firefox.json"));
    }

    [AvaloniaFact]
    public void EnsureExtensionUpToDate_ReextractsOldExtension()
    {
        using var h = new Harness();
        var integration = h.Integration;
        integration.ExtractExtension();

        // 前の版のアプリが展開したままの拡張（アプリだけ更新された状態）
        var manifest = Path.Combine(integration.ChromiumExtensionDir, "manifest.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace($"\"{BrowserIntegration.BundledExtensionVersion}\"", "\"0.0.1\""));
        File.WriteAllText(Path.Combine(integration.ChromiumExtensionDir, "background.js"), "// old");

        integration.EnsureExtensionUpToDate();
        Assert.Contains($"\"{BrowserIntegration.BundledExtensionVersion}\"", File.ReadAllText(manifest));
        Assert.NotEqual("// old", File.ReadAllText(Path.Combine(integration.ChromiumExtensionDir, "background.js")));

        // 最新なら書き直さない（ブラウザが読んでいるファイルを無駄に消さない）
        var written = File.GetLastWriteTimeUtc(manifest);
        integration.EnsureExtensionUpToDate();
        Assert.Equal(written, File.GetLastWriteTimeUtc(manifest));
    }

    [Fact]
    public void ChromiumExtensionId_MatchesManifestKey()
    {
        // manifest の "key"（公開鍵）から計算した ID が、ネイティブホストで許可している ID と一致すること
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "browser-extension", "manifest.chromium.json");
        var key = JsonNode.Parse(File.ReadAllText(manifestPath))!["key"]!.GetValue<string>();
        var hash = System.Security.Cryptography.SHA256.HashData(Convert.FromBase64String(key));
        var id = new string(Convert.ToHexStringLower(hash[..16]).Select(c => (char)('a' + Convert.ToInt32(c.ToString(), 16))).ToArray());
        Assert.Equal(NativeHost.ChromiumExtensionId, id);
    }
}
