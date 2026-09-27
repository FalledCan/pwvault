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

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is UnlockViewModel vm)
            vm.FocusPasswordRequested += OnFocusRequested;
        PasswordBox.Focus();
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
