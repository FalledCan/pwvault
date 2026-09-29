using Avalonia;

namespace PwVault.App.Views;

/// <summary>
/// 標準のタイトルバーを消してアプリの上部の帯をタイトルバーにするときの寸法。
/// OS の「最小化・最大化・閉じる」ボタン（Windows は右上、macOS は左上の信号ボタン）に重ならないよう、帯の内側に余白を取る。
/// </summary>
public static class WindowChrome
{
    public const double TitleBarHeight = 58;

    /// <summary>
    /// OS の「最小化・最大化・閉じる」ボタンの高さ。ボタンはこの高さの中で上下中央に置かれるので、
    /// 帯（<see cref="TitleBarHeight"/>）より低くして、ウィンドウの上端にぴったり付ける。
    /// </summary>
    public const double CaptionButtonsHeight = 40;

    /// <summary>上部の帯の内側の余白（上はウィンドウの縁との間を少し空ける）。</summary>
    public static Thickness TitleBarPadding { get; } = OperatingSystem.IsMacOS()
        ? new Thickness(80, 8, 12, 0)   // 左上の信号ボタンの分
        : new Thickness(12, 8, 150, 0); // 右上の 3 つのボタン（46 × 3）の分
}
