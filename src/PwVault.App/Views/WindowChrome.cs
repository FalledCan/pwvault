using Avalonia;

namespace PwVault.App.Views;

/// <summary>
/// 標準のタイトルバーを消してアプリの上部の帯をタイトルバーにするときの寸法。
/// OS の「最小化・最大化・閉じる」ボタン（Windows は右上、macOS は左上の信号ボタン）に重ならないよう、帯の内側に余白を取る。
/// </summary>
public static class WindowChrome
{
    public const double TitleBarHeight = 48;

    /// <summary>上部の帯の内側の余白。</summary>
    public static Thickness TitleBarPadding { get; } = OperatingSystem.IsMacOS()
        ? new Thickness(80, 0, 12, 0)   // 左上の信号ボタンの分
        : new Thickness(12, 0, 150, 0); // 右上の 3 つのボタン（46 × 3）の分
}
