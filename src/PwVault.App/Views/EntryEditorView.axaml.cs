using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PwVault.App.Views;

public partial class EntryEditorView : UserControl
{
    public EntryEditorView()
    {
        InitializeComponent();
        PwVault.App.Services.Shortcuts.AdaptForPlatform(this);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        TitleBox.Focus();
    }
}
