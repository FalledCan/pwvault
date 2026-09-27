using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PwVault.App.ViewModels;

namespace PwVault.App.Views;

public partial class UnlockView : UserControl
{
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

        // Windows Hello が使えて、ウィンドウが前面にあるときは、すぐに確認画面を出す
        // （隠れている間に自動ロックした場合などは出さない。戻ってきたらボタンで開始）
        await vm.QuickUnlockReady;
        if (vm.CanQuickUnlock && TopLevel.GetTopLevel(this) is Window { IsActive: true, IsVisible: true })
            vm.QuickUnlockCommand.Execute(null);
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (DataContext is UnlockViewModel vm)
            vm.FocusPasswordRequested -= OnFocusRequested;
        base.OnUnloaded(e);
    }

    // 入力欄は処理中に無効化されているので、有効化されたあとでフォーカスする
    private void OnFocusRequested(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => PasswordBox.Focus(), DispatcherPriority.Background);
}
