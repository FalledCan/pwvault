using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using PwVault.App.ViewModels;

namespace PwVault.App.Views;

/// <summary>自動タイプの選択窓。打ち込み中は隠し、失敗したら出し直す。閉じるのは ViewModel（MainViewModel.AutoTypePicker = null）から。</summary>
public partial class AutoTypePickerWindow : Window
{
    public AutoTypePickerWindow()
    {
        InitializeComponent();
        // 検索欄が空のとき、数字キー（1〜9）でその番号のアカウントを入力する（検索の文字としては入れない）
        var search = this.FindControl<TextBox>("SearchBox")!;
        search.AddHandler(TextInputEvent, (_, e) =>
        {
            if (DataContext is AutoTypePickerViewModel vm && e.Text is [>= '1' and <= '9'] digit && vm.ChooseNumber(digit[0] - '0'))
                e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>ViewModel 側で閉じたとき（×で閉じたのでなければ）の区別。</summary>
    public bool ClosingFromViewModel { get; set; }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Activate();
        Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("SearchBox")?.Focus(), DispatcherPriority.Background);
        if (DataContext is AutoTypePickerViewModel vm)
            vm.PropertyChanged += OnVmChanged;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AutoTypePickerViewModel.IsTyping) || sender is not AutoTypePickerViewModel vm) return;
        if (vm.IsTyping)
        {
            Hide();
        }
        else
        {
            Show(); // 失敗したので出し直して知らせる
            Activate();
        }
    }

    public bool IsClosed { get; private set; }

    protected override void OnClosed(EventArgs e)
    {
        IsClosed = true;
        if (DataContext is AutoTypePickerViewModel vm)
        {
            vm.PropertyChanged -= OnVmChanged;
            if (!ClosingFromViewModel) vm.CancelCommand.Execute(null); // ×で閉じた
        }
        base.OnClosed(e);
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is AutoTypePickerViewModel vm) vm.ChooseCommand.Execute(null);
    }
}
