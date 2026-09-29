using Avalonia;
using Avalonia.Controls;

namespace PwVault.App.Views;

/// <summary>「数秒後に自動で入力」の通知。前面を奪わずに画面の右下へ出す。</summary>
public partial class AutoTypeCountdownWindow : Window
{
    public AutoTypeCountdownWindow() => InitializeComponent();

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (Screens.Primary is { } screen)
        {
            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            var size = new PixelSize((int)(Bounds.Width * scale), (int)(Bounds.Height * scale));
            Position = new PixelPoint(area.Right - size.Width - 16, area.Bottom - size.Height - 16);
        }
    }
}
