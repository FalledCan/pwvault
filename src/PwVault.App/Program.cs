using Avalonia;
using PwVault.App.Bridge;
using PwVault.App.Services;

namespace PwVault.App;

sealed class Program
{
    /// <summary>多重起動の防止と、2 つ目の起動からの「前に出して」の合図。</summary>
    internal static SingleInstance? Instance { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        // ブラウザから中継役として起動された場合は、UI を作らずに 1 往復だけ中継して終わる
        if (NativeHost.IsNativeMessagingLaunch(args))
        {
            return NativeHost.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(),
                BridgeServer.DefaultPipeName, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        }

        // 同じ保管庫を 2 つのプロセスで同時に編集して上書きし合わないよう、多重起動を防ぐ。
        // 2 つ目の起動は、通知領域に隠れているかもしれない 1 つ目のウィンドウを前に出して終わる
        using var instance = new SingleInstance();
        if (!instance.IsFirstInstance)
        {
            instance.SignalFirstInstance();
            return 0;
        }
        Instance = instance;

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace();
}
