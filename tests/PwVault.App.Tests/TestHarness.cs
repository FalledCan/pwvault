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

/// <summary>一時ディレクトリ上で MainWindow と MainViewModel を組み立てる。</summary>
public sealed class Harness : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "pwvault-ui-" + Guid.NewGuid().ToString("N"));
    public FakeFileDialogs Dialogs { get; } = new();
    public MainWindow Window { get; }
    public MainViewModel Main { get; }
    public AutoLockService AutoLock { get; } = new();

    public string VaultPath => Path.Combine(Dir, "vault.pwv");

    public Harness(Action<AppSettings>? configure = null)
    {
        Directory.CreateDirectory(Dir);
        var store = new AppSettingsStore(Path.Combine(Dir, "settings.json"));
        if (configure is not null)
        {
            var s = store.Load();
            configure(s);
            store.Save(s);
        }

        Window = new MainWindow { Width = 1100, Height = 720 };
        Main = new MainViewModel(store, new ClipboardService(() => IntPtr.Zero), AutoLock, Dialogs);
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
        Main.Lock();
        Window.Close();
        AutoLock.Dispose();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}
