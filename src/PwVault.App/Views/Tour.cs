using Avalonia;
using Avalonia.Controls;

namespace PwVault.App.Views;

/// <summary>
/// チュートリアルで照らす部品に付ける目印（例: <c>views:Tour.Id="new"</c>）。
/// チュートリアルの各ページは、この目印で「どの部品を案内するか」を指す。
/// </summary>
public static class Tour
{
    public static readonly AttachedProperty<string?> IdProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Id", typeof(Tour));

    public static string? GetId(Control control) => control.GetValue(IdProperty);

    public static void SetId(Control control, string? value) => control.SetValue(IdProperty, value);
}
