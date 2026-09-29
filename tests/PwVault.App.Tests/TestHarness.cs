using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using PwVault.App;
using PwVault.App.Services;
using PwVault.Core.QuickUnlock;
using PwVault.App.ViewModels;
using PwVault.App.Views;

[assembly: AvaloniaTestApplication(typeof(PwVault.App.Tests.TestAppBuilder))]

namespace PwVault.App.Tests;

public static class TestAppBuilder
{
    // 画面のスクリーンショットを撮れるよう、Skia で実際に描画する
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>ファイル選択ダイアログの代わりに、あらかじめ積んだパスを返す。</summary>
public sealed class FakeFileDialogs : IFileDialogs
{
    public Queue<string?> Next { get; } = new();

    public Task<string?> SaveFileAsync(string title, string suggestedFileName, string filterName, string extension) =>
        Task.FromResult(Next.Count > 0 ? Next.Dequeue() : null);

    public Task<string?> OpenFileAsync(string title, string filterName, params string[] patterns) =>
        Task.FromResult(Next.Count > 0 ? Next.Dequeue() : null);
}

/// <summary>
/// サイトの代わり。Pages に「URL → (Content-Type, 中身)」を積んでおくと返し、無いものは 404。
/// 実際のネットワークには一切出ない。
/// </summary>
public sealed class FakeIconServer : HttpMessageHandler
{
    public Dictionary<string, (string Type, byte[] Body)> Pages { get; } = [];
    public List<Uri> Requests { get; } = [];

    /// <summary>テスト用の本物の PNG（拡張機能のアイコン）。</summary>
    public static byte[] Png => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "browser-extension", "icons", "32.png"));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        lock (Requests) Requests.Add(request.RequestUri!);
        if (Pages.TryGetValue(request.RequestUri!.AbsoluteUri, out var page))
        {
            var content = new ByteArrayContent(page.Body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(page.Type);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content, RequestMessage = request });
        }
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { RequestMessage = request });
    }
}

/// <summary>
/// Windows Hello の代わり。保管庫ごとの「端末の秘密」で challenge に決まった値を返す（同じ入力なら同じ出力）。
/// create のたびに秘密を作り直すのは、本物が鍵を作り直すのと同じ。
/// </summary>
public sealed class FakeHello : IQuickUnlockProvider
{
    private readonly Dictionary<Guid, byte[]> _keys = [];

    public bool Available { get; set; } = true;
    public bool UserCancels { get; set; }
    public int Prompts { get; private set; }
    public string Name => "Windows Hello";

    public Task<bool> IsAvailableAsync() => Task.FromResult(Available);

    public Task<byte[]?> SignAsync(Guid vaultId, byte[] challenge, bool create, IntPtr ownerWindow)
    {
        Prompts++;
        if (UserCancels) return Task.FromResult<byte[]?>(null);
        if (create) _keys[vaultId] = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        if (!_keys.TryGetValue(vaultId, out var key)) return Task.FromResult<byte[]?>(null);
        var sig = System.Security.Cryptography.HMACSHA512.HashData(key, challenge).Concat(new byte[192]).ToArray();
        return Task.FromResult<byte[]?>(sig);
    }

    public Task DeleteAsync(Guid vaultId)
    {
        _keys.Remove(vaultId);
        return Task.CompletedTask;
    }
}

/// <summary>
/// 自動タイプの OS 機能の代わり。前面のウィンドウはテストが決め、打ち込んだ内容は Typed に記録する（本物のキー入力はしない）。
/// </summary>
public sealed class FakeAutoType : IAutoTypePlatform
{
    public TargetWindow? Foreground { get; set; }
    public List<string> Typed { get; } = [];
    public AutoTypeHotKey? RegisteredKey { get; private set; }
    public Action? PressHotKey { get; private set; }
    public bool FailRegister { get; set; }
    public bool ActivateSucceeds { get; set; } = true;

    /// <summary>何か打ち込むたびに呼ばれる（途中で前面の画面が変わった状況を作るため）。</summary>
    public Action? AfterEachInput { get; set; }

    public TargetWindow? GetForeground() => Foreground;
    public bool Activate(IntPtr handle) => ActivateSucceeds;

    public void TypeText(string text)
    {
        Typed.Add(text);
        AfterEachInput?.Invoke();
    }

    public void PressTab()
    {
        Typed.Add("<TAB>");
        AfterEachInput?.Invoke();
    }

    public IDisposable? RegisterHotKey(AutoTypeHotKey key, Action onPressed)
    {
        if (FailRegister) return null;
        RegisteredKey = key;
        PressHotKey = onPressed;
        return new Registration(this);
    }

    private sealed class Registration(FakeAutoType owner) : IDisposable
    {
        public void Dispose()
        {
            owner.RegisteredKey = null;
            owner.PressHotKey = null;
        }
    }
}

public sealed class MutableClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>一時ディレクトリ上で MainWindow と MainViewModel を組み立てる。</summary>
public sealed class Harness : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "pwvault-ui-" + Guid.NewGuid().ToString("N"));
    public FakeFileDialogs Dialogs { get; } = new();
    public MainWindow Window { get; }
    public MainViewModel Main { get; }
    public AutoLockService AutoLock { get; } = new();

    public string VaultPath => Path.Combine(Dir, "vault.pwv");

    /// <summary>サイトからのアイコン取得の偽物。</summary>
    public FakeIconServer Icons { get; } = new();

    /// <summary>「ブラウザで開く」が呼ばれた URL。</summary>
    public List<Uri> OpenedUrls { get; } = [];

    /// <summary>Windows Hello の代わり。</summary>
    public FakeHello Hello { get; } = new();

    /// <summary>進められる時計。</summary>
    public MutableClock Clock { get; } = new();

    /// <summary>
    /// テスト用のパイプ名を作る。本物の PwVault と衝突しないように毎回変える。
    /// macOS ではパイプが一時フォルダ内のソケットファイルになり、パス全体が 104 文字以内でなければならないので短くする。
    /// </summary>
    public static string TestPipe() => "pwvt" + Guid.NewGuid().ToString("N")[..10];

    /// <summary>テスト用のパイプ名（本物の PwVault と衝突しないように毎回変える）。</summary>
    public string PipeName { get; } = TestPipe();

    /// <summary>レジストリはテスト用のキー、ファイルは一時フォルダに書く。</summary>
    public PwVault.App.Bridge.BrowserIntegration Integration { get; }
    public string RegistryBase { get; } = @"Software\PwVaultTest\" + Guid.NewGuid().ToString("N");

    /// <summary>自動タイプの OS 機能の偽物（本物のショートカットキー登録・キー入力はしない）。</summary>
    public FakeAutoType AutoType { get; } = new();

    /// <summary>一時フォルダの中だけを探す同期フォルダの探索。</summary>
    public SyncFolderLocator SyncFolders { get; }

    /// <summary>「Google ドライブ パソコン版」が入っている状態にする（ミラーモードの ~/My Drive）。同期フォルダを返す。</summary>
    public string CreateGoogleDrive() => Directory.CreateDirectory(Path.Combine(Dir, "home", "My Drive")).FullName;

    /// <summary>Nextcloud デスクトップが設定ファイルに同期フォルダを書いている状態にする。同期フォルダを返す。</summary>
    public string CreateNextcloud()
    {
        var folder = Directory.CreateDirectory(Path.Combine(Dir, "nc-sync")).FullName;
        var cfg = OperatingSystem.IsMacOS()
            ? Path.Combine(Dir, "home", "Library", "Preferences", "Nextcloud", "nextcloud.cfg")
            : Path.Combine(Dir, "appdata", "Nextcloud", "nextcloud.cfg");
        Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
        File.WriteAllText(cfg, $"[Accounts]\n0\\Folders\\1\\localPath={folder.Replace('\\', '/')}/\n0\\Folders\\1\\paused=false\n");
        return folder;
    }

    /// <summary>自動起動の登録先もテスト用（本物の Run キー・LaunchAgents には触らない）。</summary>
    public AutoStart AutoStart { get; }

    public Harness(Action<AppSettings>? configure = null)
    {
        AutoStart = new AutoStart(RegistryBase + @"\Run", Path.Combine(Dir, "LaunchAgents"));
        Integration = new PwVault.App.Bridge.BrowserIntegration(Path.Combine(Dir, "localappdata"), RegistryBase,
            macSupportRoot: Path.Combine(Dir, "Library", "Application Support"));
        Directory.CreateDirectory(Dir);
        var store = new AppSettingsStore(Path.Combine(Dir, "settings.json"));
        if (configure is not null)
        {
            var s = store.Load();
            configure(s);
            store.Save(s);
        }

        Window = new MainWindow { Width = 1100, Height = 720 };
        // テストでは通信しない（アイコンは Icons に積んだものだけ返す）・ブラウザも開かない
        // 同期フォルダ（Google ドライブ・Nextcloud）も一時フォルダの中だけを探す（本物の同期フォルダには触らない）
        SyncFolders = new SyncFolderLocator(home: Path.Combine(Dir, "home"), appData: Path.Combine(Dir, "appdata"),
            documents: Path.Combine(Dir, "documents"), driveRoots: () => []);
        Main = new MainViewModel(store, new ClipboardService(() => IntPtr.Zero), AutoLock, Dialogs, Integration, PipeName,
            new FaviconFetcher(Icons), new UpdateService(Icons), Hello, Path.Combine(Dir, "localappdata"), Clock,
            AutoStart, SyncFolders, AutoType);
        Main.OpenInBrowser = OpenedUrls.Add;
        Main.AutoTypeDelay = TimeSpan.Zero;
        Main.VaultSyncInterval = TimeSpan.FromHours(1); // テストでは SyncNow を直接呼ぶ
        Window.DataContext = Main;
        Window.Show();
        Pump();
    }

    public T Page<T>() where T : ViewModelBase => Assert.IsType<T>(Main.CurrentPage);

    /// <summary>非同期処理の途中で条件が満たされるまで UI スレッドを回す。</summary>
    public static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.True(condition(), "条件が満たされませんでした。");
    }

    public static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>現在の画面を PNG で保存する（目視確認・README 用）。</summary>
    public void Screenshot(string name)
    {
        Pump();
        var dir = Environment.GetEnvironmentVariable("PWVAULT_SCREENSHOT_DIR")
                  ?? Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
#pragma warning disable CS0618 // 12.1 で置き換え先の BitmapEncoderOptions 版が推奨になったが、既定の PNG で十分
        Window.CaptureRenderedFrame()?.Save(Path.Combine(dir, name + ".png"));
#pragma warning restore CS0618
    }

    public void Dispose()
    {
        Main.Shutdown();
        ThemeService.Apply(ThemeMode.System, AccentColor.System); // テーマはアプリ全体の状態なので、ほかのテストに持ち越さない
        if (OperatingSystem.IsWindows())
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(RegistryBase, throwOnMissingSubKey: false);
            using (var parent = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\PwVaultTest"))
                if (parent is { SubKeyCount: 0 })
                    Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(@"Software\PwVaultTest", throwOnMissingSubKey: false);
        }
        Window.ForceClose = true;
        Window.Close();
        AutoLock.Dispose();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}
