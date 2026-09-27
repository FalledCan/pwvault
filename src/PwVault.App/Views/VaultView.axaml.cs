using Avalonia.Controls;
using Avalonia.Interactivity;
using PwVault.App.ViewModels;

namespace PwVault.App.Views;

public partial class VaultView : UserControl
{
    public VaultView()
    {
        InitializeComponent();
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

    private void OnFocusSearch(object? sender, EventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }
}
