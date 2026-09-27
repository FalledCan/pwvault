using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
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
            main.OwnerWindowHandle = () => window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

            // 更新: 前回の入れ替えで残った古い exe を消し、定期的な確認を始める。入れ替えたら終了して新しい exe に任せる
            UpdateService.CleanupAfterUpdate(MainViewModel.ExePath);
            main.StartUpdateChecks();
            main.RestartRequested += (_, _) =>
            {
                window.ForceClose = true;
                desktop.Shutdown();
            };

            var tray = CreateTrayIcon(desktop, window, main);

            // 2 つ目の起動があったら、隠れているウィンドウを前に出す
            Program.Instance?.ListenForActivation(() => Dispatcher.UIThread.Post(() => ShowWindow(window)));

            // macOS: Dock のアイコンをクリックしたら（ウィンドウを隠していても）前に出す
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
                activatable.Activated += (_, e) =>
                {
                    if (e.Kind == ActivationKind.Reopen) ShowWindow(window);
                };

            // ×ボタンで閉じたときも OS のシャットダウン時も必ず呼ばれる Exit で、ロックとクリップボードの消去を行う
            desktop.Exit += (_, _) =>
            {
                main.Shutdown();
                tray.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static TrayIcon CreateTrayIcon(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, MainViewModel main)
    {
        var open = new NativeMenuItem("開く");
        open.Click += (_, _) => ShowWindow(window);
        var lockItem = new NativeMenuItem("ロック");
        lockItem.Click += (_, _) => main.Lock();
        var exit = new NativeMenuItem("終了");
        exit.Click += (_, _) =>
        {
            window.ForceClose = true;
            desktop.Shutdown();
        };

        var tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://PwVault/Assets/avalonia-logo.ico"))),
            ToolTipText = "PwVault",
            Menu = [open, lockItem, new NativeMenuItemSeparator(), exit],
        };
        tray.Clicked += (_, _) => ShowWindow(window);
        TrayIcon.SetIcons(Current!, [tray]);
        return tray;
    }

    private static void ShowWindow(Window window)
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }
}
