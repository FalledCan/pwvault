using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PwVault.App.Services;
using PwVault.App.ViewModels;
using PwVault.App.Views;

namespace PwVault.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var clipboard = new ClipboardService(() => window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
            var main = new MainViewModel(new AppSettingsStore(), clipboard, new AutoLockService(), window);
            window.DataContext = main;
            desktop.MainWindow = window;
            // ×ボタンで閉じたときも OS のシャットダウン時も必ず呼ばれる Exit で、ロックとクリップボードの消去を行う
            desktop.Exit += (_, _) => main.Shutdown();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
