using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PwVault.App.ViewModels;

namespace PwVault.App.Views;

public partial class VaultView : UserControl
{
    public VaultView()
    {
        InitializeComponent();
        PwVault.App.Services.Shortcuts.AdaptForPlatform(this);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is VaultViewModel vm)
            vm.FocusSearchRequested += OnFocusSearch;
        SearchBox.Focus();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (DataContext is VaultViewModel vm)
            vm.FocusSearchRequested -= OnFocusSearch;
        base.OnUnloaded(e);
    }

    private void OnEntryDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        // 行の上でのダブルクリックだけを扱う（スクロールバーなどは除く）
        if (e.Source is Avalonia.Visual v && v.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null)
            (DataContext as VaultViewModel)?.OnItemDoubleClicked();
    }

    private void OnFocusSearch(object? sender, EventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }
}
