using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PwVault.App.ViewModels;

namespace PwVault.App.Views;

public partial class UnlockView : UserControl
{
    private Window? _window;

    public UnlockView()
    {
        InitializeComponent();
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is not UnlockViewModel vm) return;
        vm.FocusPasswordRequested += OnFocusRequested;
        PasswordBox.Focus();

        // ウィンドウが前に出たとき（通知領域・ブラウザ拡張の「アンロックする」・2 つ目の起動など）にも確認画面を出す
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null) _window.Activated += OnWindowActivated;

        // Windows Hello が使えて、ウィンドウが前面にあるときは、すぐに確認画面を出す
        // （隠れている間に自動ロックした場合などは出さない。前に出たときに出す）
        await vm.QuickUnlockReady;
        if (_window is { IsActive: true, IsVisible: true })
            vm.TryAutoQuickUnlock();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (DataContext is UnlockViewModel vm)
            vm.FocusPasswordRequested -= OnFocusRequested;
        if (_window is not null) _window.Activated -= OnWindowActivated;
        _window = null;
        base.OnUnloaded(e);
    }

    private void OnWindowActivated(object? sender, EventArgs e) =>
        (DataContext as UnlockViewModel)?.TryAutoQuickUnlock();

    // 入力欄は処理中に無効化されているので、有効化されたあとでフォーカスする
    private void OnFocusRequested(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => PasswordBox.Focus(), DispatcherPriority.Background);
}
