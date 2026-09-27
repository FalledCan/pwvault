using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PwVault.App.Views;

public partial class SetupView : UserControl
{
    public SetupView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        PasswordBox.Focus();
    }
}
