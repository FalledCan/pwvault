using Avalonia;

namespace PwVault.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        // 同じ保管庫を 2 つのプロセスで同時に編集して上書きし合わないよう、多重起動を防ぐ
        using var mutex = new Mutex(initiallyOwned: true, @"Local\PwVault.SingleInstance", out var createdNew);
        if (!createdNew)
            return 1;

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
