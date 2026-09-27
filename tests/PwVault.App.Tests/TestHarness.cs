using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using PwVault.App;
using PwVault.App.Services;
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

    /// <summary>テスト用のパイプ名（本物の PwVault と衝突しないように毎回変える）。</summary>
    public string PipeName { get; } = "PwVault.Test." + Guid.NewGuid().ToString("N");

    /// <summary>レジストリはテスト用のキー、ファイルは一時フォルダに書く。</summary>
    public PwVault.App.Bridge.BrowserIntegration Integration { get; }
    public string RegistryBase { get; } = @"Software\PwVaultTest\" + Guid.NewGuid().ToString("N");

    public Harness(Action<AppSettings>? configure = null)
    {
        Integration = new PwVault.App.Bridge.BrowserIntegration(Path.Combine(Dir, "localappdata"), RegistryBase);
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
        Main = new MainViewModel(store, new ClipboardService(() => IntPtr.Zero), AutoLock, Dialogs, Integration, PipeName,
            new FaviconFetcher(Icons), new UpdateService(Icons));
        Main.OpenInBrowser = OpenedUrls.Add;
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
